using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// Text recognition through the offline OCR engine bundled with WeChat (Weixin) 4.x. The engine
/// lives inside the user's WeChat install (%APPDATA%\Tencent\XPlugin + mmmojo_64.dll); we drive it
/// through the wcocr shim (github.com/swigger/wechat-ocr, dll shipped in Native\wcocr.dll), which
/// spawns WeChat's own OCR worker and talks to it the same way WeChat does. Nothing goes online
/// and WeChat itself does not need to be running or logged in. Quality is markedly better than
/// the Windows built-in OCR for Chinese documents.
/// </summary>
public sealed class WeChatOcrEngine : IOcrEngine
{
    /// <summary>wcocr hands us the JSON answer as a UTF-8 C string through this callback.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetResultDelegate(IntPtr utf8Json);

    private const string NativeDll = "wcocr";

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool wechat_ocr(
        [MarshalAs(UnmanagedType.LPWStr)] string ocrExe,
        [MarshalAs(UnmanagedType.LPWStr)] string wechatDir,
        byte[] imageFileNameUtf8,
        SetResultDelegate callback);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void stop_ocr();

    private static int _shutdownHooked;

    private readonly string _ocrExePath;
    private readonly string _wechatDir;
    private readonly string _displayReason;

    /// <summary>One OCR worker process only — recognition calls must not overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Rooted so the native callback can never race a GC during a call.</summary>
    private readonly SetResultDelegate _callback;

    private string? _resultJson;

    public WeChatOcrEngine(string ocrExePath, string wechatDir, string displayReason = "")
    {
        _ocrExePath = ocrExePath;
        _wechatDir = wechatDir;
        _displayReason = displayReason;
        _callback = OnResult;
    }

    public string DisplayName => string.IsNullOrEmpty(_displayReason) ? "微信OCR" : $"微信OCR（{_displayReason}）";

    /// <summary>
    /// Locates the WeChat OCR engine on this machine. Returns false when WeChat is absent, the
    /// process is not 64-bit, or the wcocr shim is missing — the caller then keeps using the
    /// built-in Windows OCR.
    /// </summary>
    public static bool TryLocate(out WeChatOcrEngine? engine, out string reason)
    {
        engine = null;
        if (!Environment.Is64BitProcess)
        {
            reason = "需要 64 位进程";
            return false;
        }

        // The shim is bundled into single-file publishes and auto-extracted by the host into a
        // temp folder, so a plain File.Exists cannot see it — the only reliable check is to let
        // the P/Invoke loader resolve it. stop_ocr() is a no-op on an uninitialised shim.
        try
        {
            stop_ocr();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            reason = ex is DllNotFoundException ? "缺少 wcocr.dll" : "wcocr.dll 架构不匹配";
            return false;
        }

        if (!TryFindOcrPlugin(out string? ocrExe, out string? pluginReason))
        {
            reason = pluginReason!;
            return false;
        }

        if (!TryFindWeChatRuntime(out string? wechatDir, out string? runtimeReason))
        {
            reason = runtimeReason!;
            return false;
        }

        reason = string.Empty;
        engine = new WeChatOcrEngine(ocrExe!, wechatDir!);
        return true;
    }

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        // The wcocr interface takes a file path, so park the rendered page in %TEMP% first.
        string imageFile = Path.Combine(Path.GetTempPath(), $"wm-ocr-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imageFile, imageBytes).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref _resultJson, null);
                byte[] imageFileName = Encoding.UTF8.GetBytes(imageFile + "\0");

                // Blocking call: wcocr spawns the OCR worker on first use and waits for the answer.
                bool ok = await Task.Run(() => wechat_ocr(_ocrExePath, _wechatDir, imageFileName, _callback))
                    .ConfigureAwait(false);

                string? json = Volatile.Read(ref _resultJson);
                if (!ok)
                    throw new InvalidOperationException("微信 OCR 引擎调用失败（引擎文件可能不完整或版本不匹配）。");
                if (json is null)
                    throw new InvalidOperationException("微信 OCR 引擎未返回结果。");

                HookShutdown();
                return ParseLines(json);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            try { File.Delete(imageFile); } catch { /* temp cleanup is best effort */ }
        }
    }

    private void OnResult(IntPtr utf8Json)
    {
        int length = 0;
        while (Marshal.ReadByte(utf8Json, length) != 0) length++;
        var bytes = new byte[length];
        Marshal.Copy(utf8Json, bytes, 0, length);
        Volatile.Write(ref _resultJson, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Result JSON: {"errcode":0,"ocr_response":[{"text":"…","left":…,…}, …]}.</summary>
    private static IReadOnlyList<string> ParseLines(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("微信 OCR 返回了无法解析的结果。");

            if (root.TryGetProperty("errcode", out JsonElement errcode) &&
                errcode.ValueKind == JsonValueKind.Number &&
                errcode.TryGetInt32(out int code) &&
                code != 0)
            {
                throw new InvalidOperationException($"微信 OCR 返回错误码 {code}。");
            }

            if (!root.TryGetProperty("ocr_response", out JsonElement blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String &&
                    text.GetString() is { Length: > 0 } line)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("微信 OCR 返回了无法解析的结果。", ex);
        }
    }

    /// <summary>Kills the OCR worker process when the app exits; registered once, after first use.</summary>
    private static void HookShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { stop_ocr(); } catch { /* process is going away anyway */ }
            };
    }

    /// <summary>
    /// The OCR plugin is unpacked by WeChat under %APPDATA%\Tencent\*\XPlugin\Plugins\WeChatOCR\
    /// &lt;build&gt;\extracted — Weixin 4.x ships wxocr.dll, legacy 3.x ships WeChatOCR.exe.
    /// </summary>
    private static bool TryFindOcrPlugin(out string? ocrExe, out string? reason)
    {
        ocrExe = null;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] roots =
        {
            Path.Combine(appData, "Tencent", "xwechat", "XPlugin", "Plugins", "WeChatOCR"),
            Path.Combine(appData, "Tencent", "WeChat", "XPlugin", "Plugins", "WeChatOCR"),
            Path.Combine(appData, "Tencent", "Weixin", "XPlugin", "Plugins", "WeChatOCR"),
        };

        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string build in Directory.EnumerateDirectories(root)
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string extracted = Path.Combine(build, "extracted");
                string wxocr = Path.Combine(extracted, "wxocr.dll");
                if (File.Exists(wxocr))
                {
                    ocrExe = wxocr;
                    reason = null;
                    return true;
                }

                string legacy = Path.Combine(extracted, "WeChatOCR.exe");
                if (File.Exists(legacy))
                {
                    ocrExe = legacy;
                    reason = null;
                    return true;
                }
            }
        }

        reason = "未找到微信 OCR 插件";
        return false;
    }

    /// <summary>
    /// The runtime folder holds mmmojo_64.dll (Weixin 4.x) or mmmojo.dll (WeChat 3.x). Read from
    /// the registry install path first, then fall back to the default Program Files layout.
    /// </summary>
    private static bool TryFindWeChatRuntime(out string? wechatDir, out string? reason)
    {
        foreach (string registryKey in new[] { @"Software\Tencent\Weixin", @"Software\Tencent\WeChat" })
        {
            string? installPath = ReadInstallPath(registryKey);
            if (installPath is not null && TryPickRuntimeFolder(installPath, out wechatDir))
            {
                reason = null;
                return true;
            }
        }

        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Path.Combine(Environment.GetFolderPath(folder), "Tencent");
            if (!Directory.Exists(root)) continue;

            foreach (string product in new[] { "Weixin", "WeChat" })
            {
                string candidate = Path.Combine(root, product);
                if (Directory.Exists(candidate) && TryPickRuntimeFolder(candidate, out wechatDir))
                {
                    reason = null;
                    return true;
                }
            }
        }

        wechatDir = null;
        reason = "未找到微信安装目录（mmmojo 运行库）";
        return false;
    }

    private static string? ReadInstallPath(string registryKey)
    {
        try
        {
            return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey)?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Picks the highest version subfolder that still contains the mojo runtime dll.</summary>
    private static bool TryPickRuntimeFolder(string installPath, out string? wechatDir)
    {
        wechatDir = null;
        if (!Directory.Exists(installPath)) return false;

        wechatDir = Directory.EnumerateDirectories(installPath)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(dir =>
                File.Exists(Path.Combine(dir, "mmmojo_64.dll")) ||
                File.Exists(Path.Combine(dir, "mmmojo.dll")));

        return wechatDir is not null;
    }
}
