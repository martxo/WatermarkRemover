using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// Embedded LAN web server for phone → PC transfers (the 「手机传文件」 page). Serves one upload
/// page and accepts multipart/form-data posts, streaming each part straight to disk so multi-GB
/// phone videos never buffer in memory.
///
/// Built on the built-in <see cref="HttpListener"/> (kernel-mode http.sys) instead of a
/// hand-rolled <see cref="TcpListener"/> HTTP parser. On the dev box, a raw socket server inside
/// the WPF process completed TCP handshakes but never delivered a single payload byte to
/// user-mode reads — blocking or async alike — while the very same code shape in a plain console
/// process echoed data fine and http.sys served data fine, so the mature kernel-side HTTP stack
/// wins: it is Microsoft-maintained, battle-tested, and owns none of the broken user-mode socket
/// path. A non-loopback prefix needs a one-time per-machine reservation;
/// <see cref="TryElevatedSetup"/> self-provisions it (URLACL + firewall port rule) through a
/// single UAC prompt, keeping the app double-click portable.
///
/// Access control is a per-session token in the URL (<c>?t=…</c>): the QR code is the only way
/// the page is normally reached, and another LAN device guessing the address without it gets a
/// 403 before any body is read. Received files are de-duplicated with
/// <see cref="OutputNameGenerator.FindAvailablePath"/> and announced through
/// <see cref="FileReceived"/> on a handler thread — subscribers marshal to their own context.
/// </summary>
public sealed class LanTransferServer : IDisposable
{
    /// <summary>Raised on a handler thread after a file finished landing on disk.</summary>
    public event Action<string, long>? FileReceived;

    /// <summary>Raised on a handler thread when the phone posts plain text; the payload carries
    /// the text plus its chat-style metadata (sender, server time). Subscribers marshal to
    /// their own context.</summary>
    public event Action<TextMessage>? TextReceived;

    /// <summary>Raised when the upload page is served — the phone scanned the QR and opened it.</summary>
    public event Action? PageOpened;

    /// <summary>Raised when a multipart upload begins; the parameter is the declared body size.</summary>
    public event Action<long>? UploadStarted;

    /// <summary>Throttled upload progress (received / total body bytes), at most every 500 ms.</summary>
    public event Action<long, long>? UploadProgress;

    /// <summary>Raised when an upload fails or the connection drops mid-transfer; the parameter
    /// is a status-bar friendly phrase.</summary>
    public event Action<string>? UploadFailed;

    /// <summary>Fixed LAN port: deterministic for the URL reservation, and obscure enough to
    /// almost never collide.</summary>
    private const int DefaultPort = 52125;

    /// <summary>Firewall rule installed by <see cref="TryElevatedSetup"/>; probed by name.</summary>
    private const string FirewallRuleName = "WatermarkRemover-LanTransfer";

    /// <summary>Copy-paste texts are small; anything bigger is treated as a mistake.</summary>
    private const int MaxTextBytes = 1 << 20;

    private readonly Action<string> _trace;
    private readonly LanTransferServerOptions _options;

    private HttpListener? _listener;
    private Thread? _contextThread;
    private string? _token;
    private volatile int _busy; // 1 while an upload is being written (single-phone flow)

    /// <summary>Upper bound for one upload request; a stalled phone can't hold the receiver
    /// forever. Generous: phone videos over Wi-Fi may take minutes per GB.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    public LanTransferServer(LanTransferServerOptions? options = null, Action<string>? trace = null)
    {
        _options = options ?? new LanTransferServerOptions();
        _trace = trace ?? (_ => { });
    }

    /// <summary>The address to encode into the QR code; valid after <see cref="Start"/>.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>The bound local port (valid even when the advertised host is a LAN address).</summary>
    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Registers the HTTP prefix and starts the request loop. Files land in
    /// <paramref name="targetDirectory"/>; the directory is created when missing. A preferred
    /// port of 0 (tests) binds loopback only; otherwise the LAN-wide prefix is used and, when the
    /// machine lacks the one-time reservation, provisioned through a single UAC prompt.
    /// </summary>
    public void Start(string targetDirectory)
    {
        if (IsRunning) throw new InvalidOperationException("接收服务已在运行。");

        Directory.CreateDirectory(targetDirectory);
        _options.TargetDirectory = targetDirectory;
        _token = Guid.NewGuid().ToString("N")[..8];

        bool wildcard;
        int port;
        if (_options.PreferredPort is int preferred && preferred > 0)
        {
            port = preferred;
            wildcard = true;
        }
        else if (_options.PreferredPort == 0)
        {
            port = GrabFreePort();
            wildcard = false; // tests: a loopback prefix needs no admin reservation
        }
        else
        {
            port = DefaultPort;
            wildcard = true;
        }

        _listener = StartListener(port, wildcard);
        Port = port;
        Url = $"http://{(wildcard ? LanAddresses.BestIPv4() : "localhost")}:{port}/?t={_token}";
        IsRunning = true;

        // The listener works even without our firewall rule — Windows just nags the user with a
        // security alert on EVERY start ("是否允许访问网络"), whose one-click rule never covers
        // every network profile. Check coverage in the background and self-provision once; after
        // that single UAC the alert is gone for good.
        if (wildcard)
            _ = Task.Run(() => EnsureFirewallCoverage(port));

        _contextThread = new Thread(ContextLoop) { IsBackground = true, Name = "LanTransferHttp" };
        _contextThread.Start();
        _trace($"手机传文件：监听端口 {port}，保存目录 {targetDirectory}");
    }

    /// <summary>Starts the listener, self-provisioning the URL reservation when missing.</summary>
    private HttpListener StartListener(int port, bool wildcard)
    {
        string prefix = wildcard ? $"http://+:{port}/" : $"http://localhost:{port}/";

        var first = new HttpListener();
        first.Prefixes.Add(prefix);
        try
        {
            first.Start();
            return first;
        }
        catch (HttpListenerException ex) when (wildcard && ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            first.Close();
            if (!TryElevatedSetup(port))
                throw new InvalidOperationException(
                    "开放局域网访问需要一次性管理员授权：请在弹出的 UAC 窗口点「是」后重试。", ex);
        }
        catch (HttpListenerException ex) // e.g. ERROR_SHARING_VIOLATION: port already claimed
        {
            first.Close();
            throw new InvalidOperationException($"端口 {port} 被其他程序占用，无法开启接收服务。", ex);
        }

        var retry = new HttpListener();
        retry.Prefixes.Add(prefix);
        retry.Start(); // still denied → the raw HttpListenerException tells why
        return retry;
    }

    /// <summary>
    /// One-time per-machine setup for the LAN prefix: an http.sys URL reservation plus a firewall
    /// rule for THIS executable on the transfer port, all network profiles. Idempotent (stale
    /// reservations/rules are deleted first — the previous add-only version could never refresh
    /// a rule after the exe moved, and could never run at all when only the reservation predated
    /// it, which is exactly how machines ended up nagged forever). Runs one elevated command —
    /// the user sees a single UAC prompt; declining keeps the receiver working, just nagged.
    /// </summary>
    private static bool TryElevatedSetup(int port)
    {
        try
        {
            string exe = AppExePath;
            var startInfo = new ProcessStartInfo("cmd.exe",
                $"/c netsh http delete urlacl url=http://+:{port}/ & " +
                $"netsh http add urlacl url=http://+:{port}/ sddl=D:(A;;GX;;;WD) & " +
                $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" & " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
                $"dir=in action=allow protocol=TCP localport={port} profile=any program=\"{exe}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            return process is not null && process.WaitForExit(20_000) && process.ExitCode == 0;
        }
        catch
        {
            return false; // UAC declined or elevation unavailable
        }
    }

    /// <summary>True when our rule exists AND binds the current exe — a rule left behind by an
    /// older install location does not cover this binary, so Windows would keep nagging.</summary>
    private static bool FirewallRuleCovers()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe",
                $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            // ExitCode 1 = "no rules match" (locale-independent); the program line is checked by
            // path so a moved portable install re-provisions instead of trusting the stale rule.
            return process.ExitCode == 0
                && output.Contains(AppExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // can't tell → treat as uncovered; the elevated path repairs it
        }
    }

    private static void EnsureFirewallCoverage(int port)
    {
        if (FirewallRuleCovers()) return;
        TryElevatedSetup(port);
    }

    private static string AppExePath =>
        Environment.ProcessPath
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private static int GrabFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        try { _listener?.Close(); } catch { /* already gone */ }
        _listener = null;
        _trace("手机传文件：接收服务已停止");
    }

    public void Dispose() => Stop();

    private void ContextLoop()
    {
        var listener = _listener!;
        while (IsRunning && ReferenceEquals(_listener, listener))
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext(); // blocking; Stop() aborts it via listener close
            }
            catch (Exception ex) when (!IsRunning)
            {
                _trace($"手机传文件：停止监听（{ex.Message}）");
                break;
            }
            catch (Exception ex)
            {
                _trace($"手机传文件：获取请求失败（{ex.Message}）");
                continue;
            }

            ThreadPool.QueueUserWorkItem(_ => HandleContext(context));
        }
    }

    private void HandleContext(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            if (!TokenMatches(request.Url?.Query))
            {
                WriteText(response, 403, "text/plain; charset=utf-8",
                    "请在电脑上重新扫码获取访问地址");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WriteText(response, 200, "text/html; charset=utf-8", MobileUploadPage.Html);
                PageOpened?.Invoke();
            }
            else if (request.Url?.AbsolutePath == "/text")
                HandleText(request, response);
            else
                HandleUpload(request, response);
        }
        catch (Exception ex)
        {
            _trace($"手机传文件：请求处理失败（{ex.GetType().Name}：{ex.Message}）");
            try { response.Abort(); } catch { /* connection already gone */ }
            return;
        }

        try { response.Close(); } catch { /* client gone */ }
    }

    private bool TokenMatches(string? query)
    {
        if (_token is null) return false;
        foreach (var pair in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "t" && pair[(eq + 1)..] == _token) return true;
        }
        return false;
    }

    /// <summary>Streams a multipart/form-data body to disk, one file per file part.</summary>
    private void HandleUpload(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? boundary = MultipartBoundary(request.ContentType);
        if (boundary is null || request.ContentLength64 <= 0)
        {
            WriteText(response, 400, "text/plain; charset=utf-8",
                "请求不是有效的文件上传");
            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            WriteText(response, 409, "text/plain; charset=utf-8",
                "另一个传输正在进行，请稍后再试");
            return;
        }

        UploadStarted?.Invoke(request.ContentLength64);
        int saved = 0;
        var started = Stopwatch.StartNew();
        long total = request.ContentLength64;
        long received = 0;
        long lastEmit = Environment.TickCount64;
        void Report()
        {
            if (Environment.TickCount64 - lastEmit < 500) return; // throttle: UI can't use more
            lastEmit = Environment.TickCount64;
            UploadProgress?.Invoke(received, total);
        }

        try
        {
            var reader = new MultipartBodyReader(request.InputStream, request.ContentLength64, boundary);
            reader.SkipPreamble();

            while (true)
            {
                if (started.Elapsed > RequestTimeout) throw new TimeoutException("传输超时");

                var headers = reader.ReadHeaders();
                string? filename = MultipartBodyReader.ExtractFilename(headers);

                string? target = filename is null ? null : ResolveTarget(filename);
                long bytes = 0;
                var buffer = new byte[1 << 16];
                int read;
                if (target is not null)
                {
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    while ((read = reader.ReadContent(buffer)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        bytes += read;
                        received += read;
                        Report();
                    }
                }
                else
                {
                    while ((read = reader.ReadContent(buffer)) > 0) { received += read; Report(); }
                }

                if (target is not null)
                {
                    saved++;
                    _trace($"手机传文件：已接收 {Path.GetFileName(target)}（{bytes / 1024.0:F1} KB）");
                    FileReceived?.Invoke(target, bytes);
                }

                if (!reader.NextPart()) break;
            }

            WriteText(response, 200, "text/plain; charset=utf-8",
                $"已接收 {saved} 个文件，可继续选择上传");
        }
        catch (Exception ex)
        {
            UploadFailed?.Invoke(DescribeUploadError(ex));
            throw; // HandleContext traces and aborts the response
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Turns an upload failure into a status-bar friendly phrase.</summary>
    private static string DescribeUploadError(Exception ex) => ex switch
    {
        TimeoutException => "传输超时（超过 30 分钟上限）",
        IOException or SocketException => "连接中断（手机取消或 Wi-Fi 不稳定）",
        InvalidDataException => "上传数据不完整",
        _ => ex.Message,
    };

    /// <summary>Reads the phone's plain UTF-8 text body and relays it via <see cref="TextReceived"/>.
    /// The JSON reply returns the server-side time so the phone's own history uses one clock.</summary>
    private void HandleText(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 is <= 0 or > MaxTextBytes)
        {
            WriteText(response, 400, "text/plain; charset=utf-8", "文本为空或超出大小限制");
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 14))
            body = reader.ReadToEnd();

        var message = new TextMessage(
            body,
            DescribeSender(request.UserAgent, request.RemoteEndPoint?.Address?.ToString()),
            DateTime.Now);

        _trace($"手机传文件：已接收文本（{body.Length} 字，来自 {message.SenderLabel}）");
        TextReceived?.Invoke(message);
        WriteText(response, 200, "application/json; charset=utf-8",
            $$"""{"ok":true,"time":"{{message.ReceivedAt:HH:mm}}"}""");
    }

    /// <summary>Builds a short, human sender label from the phone's browser UA and IP tail —
    /// enough to tell senders apart in the message list, no deeper device probing.</summary>
    internal static string DescribeSender(string? userAgent, string? remoteAddress)
    {
        string device;
        if (string.IsNullOrEmpty(userAgent))
            device = "手机";
        else if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) device = "iPhone";
        else if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) device = "iPad";
        else if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            var version = Regex.Match(userAgent, @"Android\s+([\d.]+)");
            device = version.Success ? $"Android {version.Groups[1].Value}" : "Android";
        }
        else if (userAgent.Contains("Windows NT", StringComparison.OrdinalIgnoreCase)) device = "Windows";
        else if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) device = "Mac";
        else device = "手机";

        if (userAgent?.Contains("MicroMessenger", StringComparison.OrdinalIgnoreCase) == true)
            device += " 微信";

        return string.IsNullOrEmpty(remoteAddress) ? device : $"{device} · …{AddressTail(remoteAddress!)}";
    }

    /// <summary>Last usable segment of an IP string, for the compact 「…31」 style tail.</summary>
    private static string AddressTail(string address)
    {
        var segment = address.Split(address.Contains(':') ? ':' : '.')[^1];
        return segment.Length <= 4 ? segment : segment[^4..];
    }

    private string ResolveTarget(string clientFilename)
    {
        string fileName = SanitizeFileName(clientFilename);
        return OutputNameGenerator.FindAvailablePath(_options.TargetDirectory, fileName);
    }

    /// <summary>Extracts the boundary token from a multipart Content-Type header.</summary>
    internal static string? MultipartBoundary(string? contentType)
    {
        if (contentType is null) return null;
        foreach (var segment in contentType.Split(';'))
        {
            var trimmed = segment.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 9)
                return trimmed[9..].Trim('"');
        }
        return null;
    }

    /// <summary>Strips any path from a client-supplied name and blanks invalid characters.</summary>
    internal static string SanitizeFileName(string name)
    {
        name = name.Replace("\\", "/");
        name = Path.GetFileName(name.Trim()); // kills device prefixes ("C:"), traversal and sibling paths
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0) cleaned = "未命名";
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    /// <summary>Writes head + UTF-8 body; the caller owns <c>Close</c>/<c>Abort</c>.</summary>
    private static void WriteText(HttpListenerResponse response, int status, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = payload.Length;
        response.KeepAlive = false;
        response.OutputStream.Write(payload, 0, payload.Length);
    }
}

/// <summary>Wiring for <see cref="LanTransferServer"/>; mutable because the target directory is
/// only known at <c>Start</c> time.</summary>
public sealed class LanTransferServerOptions
{
    /// <summary>Where received files are written (set by <see cref="LanTransferServer.Start"/>).</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>Fixed LAN port; 0 = loopback-only ephemeral port (tests); null = the built-in
    /// default port.</summary>
    public int? PreferredPort { get; set; }
}

/// <summary>
/// Single-buffer streaming reader over a multipart/form-data body. Owns the only read cursor on
/// the underlying stream (parts are never surfaced as separate streams — consumers pull through
/// <see cref="ReadContent"/>), so no bytes are lost or duplicated between sections. The
/// delimiter is <c>\r\n--boundary</c>: part content ends cleanly with no stray CRLF, and the
/// body possibly starting with the boundary directly (empty preamble) is special-cased.
/// </summary>
internal sealed class MultipartBodyReader
{
    private readonly Stream _source;
    private readonly byte[] _delimiter;
    private readonly byte[] _buffer = new byte[1 << 16];
    private int _buffered;      // valid bytes in _buffer
    private int _position;      // read cursor inside _buffer
    private long _remaining;    // body bytes still allowed from the source
    private bool _sourceEnded;

    public MultipartBodyReader(Stream source, long contentLength, string boundary)
    {
        _source = source;
        _remaining = contentLength;
        _delimiter = Encoding.UTF8.GetBytes("\r\n--" + boundary);
    }

    /// <summary>
    /// Consumes the preamble and the first boundary line (through its CRLF), leaving the reader
    /// at the first part's headers.
    /// </summary>
    public void SkipPreamble()
    {
        // Bodies usually start directly with the boundary (no CRLF); browsers may also send a
        // preamble. Peek two bytes: "--" at the very start is the first boundary itself.
        int first = ReadByte();
        int second = first >= 0 ? ReadByte() : -1;
        if (first == '-' && second == '-')
        {
            // Boundary line just started: consume the rest of the line (boundary + CRLF).
            SkipLine();
            return;
        }

        // Preamble bytes — push them back and scan for the first delimiter.
        _position -= 2;
        SkipToDelimiter();
        // Delimiter consumed ("\r\n--boundary"); drop the rest of the boundary line (CRLF).
        SkipLine();
    }

    /// <summary>Reads the part's header block (all lines up to the blank one).</summary>
    public string[] ReadHeaders()
    {
        var headers = new List<string>();
        while (true)
        {
            string line = ReadLine();
            if (line.Length == 0) break;
            headers.Add(line);
        }
        return headers.ToArray();
    }

    /// <summary>
    /// Reads part content into <paramref name="target"/>. Returns 0 when the part is finished
    /// (the <c>\r\n--boundary</c> delimiter was reached and consumed). Never exposes delimiter
    /// bytes, and never reads beyond them.
    /// </summary>
    public int ReadContent(byte[] target)
    {
        while (true)
        {
            int available = _buffered - _position;
            if (available == 0)
            {
                if (!Fill())
                    return 0; // malformed end (source ended without a delimiter): treat as part end
                continue;
            }

            int match = IndexOf(_buffer, _position, available, _delimiter);
            if (match >= 0)
            {
                int count = Math.Min(match - _position, target.Length);
                if (count > 0)
                {
                    Buffer.BlockCopy(_buffer, _position, target, 0, count);
                    _position += count;
                    return count;
                }
                _position = match + _delimiter.Length; // consume the delimiter itself: part done
                return 0;
            }

            // Hold back a possible partial delimiter at the tail (only while source remains);
            // held bytes are re-examined after the next fill, so nothing is lost or duplicated.
            int hold = _sourceEnded ? 0 : Math.Min(_delimiter.Length - 1, available);
            int emit = Math.Min(available - hold, target.Length);
            if (emit > 0)
            {
                Buffer.BlockCopy(_buffer, _position, target, 0, emit);
                _position += emit;
                return emit;
            }

            if (_sourceEnded) return 0;

            // Buffer holds only a partial delimiter — compact to the front and refill.
            Array.Copy(_buffer, _buffered - available, _buffer, 0, available);
            _buffered = available;
            _position = 0;
            if (!Fill()) continue; // source ended mid-tail: re-scan
        }
    }

    /// <summary>
    /// Consumes what follows the boundary just passed: "--" = final (returns false);
    /// CRLF = another part follows (returns true).
    /// </summary>
    public bool NextPart()
    {
        int first = ReadByte();
        if (first == '-')
        {
            ReadByte();
            return false; // final "--" (transport padding after it is irrelevant)
        }

        // Otherwise the boundary line ended with CRLF — consume it and continue.
        if (first == '\r')
            ReadByte(); // \n
        return true;
    }

    /// <summary>Extracts <c>filename</c> (RFC 7578) out of Content-Disposition; understands the
    /// RFC 5987 <c>filename*=UTF-8''…</c> extended form used by modern mobile browsers.</summary>
    internal static string? ExtractFilename(string[] headers)
    {
        string? disposition = headers.FirstOrDefault(h =>
            h.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase));
        if (disposition is null) return null;

        string? extended = null, simple = null;
        foreach (var segment in disposition.Split(';'))
        {
            var trimmed = segment.Trim();
            if (trimmed.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
                extended = trimmed[10..].Trim('"');
            else if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                simple = trimmed[9..].Trim('"');
        }

        if (extended is not null)
        {
            // form: charset''percent-encoded-name
            int split = extended.IndexOf('\'');
            if (split >= 0)
            {
                try { return Uri.UnescapeDataString(extended[(extended.LastIndexOf('\'') + 1)..]); }
                catch (UriFormatException) { /* fall through to the simple form */ }
            }
        }
        return simple;
    }

    // --- buffered primitives ---

    private void SkipToDelimiter()
    {
        while (true)
        {
            int available = _buffered - _position;
            if (available == 0)
            {
                if (!Fill())
                    throw new InvalidDataException("multipart 正文缺少结束边界");
                continue;
            }

            int match = IndexOf(_buffer, _position, available, _delimiter);
            if (match >= 0)
            {
                _position = match + _delimiter.Length;
                return;
            }

            if (_sourceEnded)
                throw new InvalidDataException("multipart 正文缺少结束边界");

            // Keep only a possible partial delimiter at the tail, compact to the front, refill.
            int keep = Math.Min(_delimiter.Length - 1, available);
            Array.Copy(_buffer, _buffered - keep, _buffer, 0, keep);
            _buffered = keep;
            _position = 0;
            Fill();
        }
    }

    private int ReadByte()
    {
        if (_position >= _buffered && !Fill())
            return -1;
        return _buffer[_position++];
    }

    /// <summary>Reads one CRLF-terminated line. Decoded as UTF-8: Android Chrome sends raw
    /// UTF-8 bytes inside <c>filename="…"</c>, and part headers must not be mojibake'd.</summary>
    private string ReadLine()
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            int b = ReadByte();
            if (b < 0) break;
            if (b == '\n') break;
            if (b != '\r') bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private void SkipLine() => _ = ReadLine();

    /// <summary>Appends more source bytes to the buffer (held-back tail bytes are preserved),
    /// bounded by the body's declared Content-Length.</summary>
    private bool Fill()
    {
        if (_sourceEnded || _remaining <= 0)
        {
            _sourceEnded = true;
            return false;
        }

        int max = (int)Math.Min(_buffer.Length - _buffered, _remaining);
        if (max <= 0) return false; // buffer full of a partial delimiter can't happen: callers compact first

        int read = _source.Read(_buffer, _buffered, max);
        if (read <= 0)
        {
            _sourceEnded = true;
            return false;
        }
        _remaining -= read;
        _buffered += read;
        return true;
    }

    private static int IndexOf(byte[] haystack, int start, int length, byte[] needle)
    {
        int last = start + length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
