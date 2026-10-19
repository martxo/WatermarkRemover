using System.Diagnostics;
using System.Text.Json;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine.BrotherEngine;

/// <summary>
/// One scan job driven through the x86 bridge process. Pages arrive as NDJSON events on the
/// child's stdout and are surfaced through <see cref="ReadPageAsync"/> exactly like the other
/// sources do, so the downstream pipeline is unaware of the channel.
/// </summary>
internal sealed class BrotherEngineSession : IScanSession
{
    private readonly Process _process;
    private readonly ScanDeviceInfo _device;
    private readonly Action<string, Exception?>? _trace;
    private bool _completed;

    public BrotherEngineSession(
        string helperPath,
        object request,
        ScanDeviceInfo device,
        Action<string, Exception?>? trace)
    {
        _device = device;
        _trace = trace;
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = "--scan",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory,
        };
        _process = new Process { StartInfo = startInfo };
        _request = JsonSerializer.Serialize(request);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _trace?.Invoke($"[Brother 引擎] {e.Data}", null);
            }
        };
    }

    private readonly string _request;

    public ScanDeviceInfo Device => _device;

    public void Start(CancellationToken cancellationToken)
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException("Brother 引擎桥接进程启动失败。");
        }

        _process.BeginErrorReadLine();
        _process.StandardInput.WriteLine(_request);
        _process.StandardInput.Flush();
        _trace?.Invoke("Brother 引擎会话已建立（ADK 直连通道）。", null);
        _ = cancellationToken;
    }

    public async Task<RasterImage?> ReadPageAsync(CancellationToken cancellationToken = default)
    {
        while (!_completed)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // stdout closed before a done marker: the child crashed or was killed.
                _completed = true;
                var exitCode = WaitForExitSilently();
                throw new InvalidOperationException(
                    $"Brother 引擎桥接进程提前退出（exit {exitCode}），未收到完成标记。");
            }

            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("t").GetString();
            switch (type)
            {
                case "page":
                    var width = doc.RootElement.GetProperty("w").GetInt32();
                    var height = doc.RootElement.GetProperty("h").GetInt32();
                    var b64 = doc.RootElement.GetProperty("b64").GetString() ?? string.Empty;
                    var payload = Convert.FromBase64String(b64);
                    var enc = doc.RootElement.TryGetProperty("enc", out var encElement)
                        ? encElement.GetString()
                        : null;
                    if (enc is "jpeg")
                    {
                        // The bridge passes JPEG pages through untouched; decode them here where
                        // Skia is available (the x86 bridge carries no imaging dependency).
                        _trace?.Invoke($"[Brother 引擎] JPEG 页 {payload.Length / 1024.0:F0} KB，解码为位图。", null);
                        return SkiaRaster.Decode(payload);
                    }

                    return new RasterImage(width, height, payload);

                case "blank":
                    continue; // engine-side blank detection; keep pulling until done

                case "done":
                    _completed = true;
                    return null;

                case "aborted":
                    _completed = true;
                    return null;

                case "error":
                    _completed = true;
                    var message = doc.RootElement.TryGetProperty("msg", out var msg)
                        ? msg.GetString() ?? "未知错误"
                        : "未知错误";
                    throw new InvalidOperationException($"Brother 引擎扫描失败：{message}");

                default:
                    _trace?.Invoke($"[Brother 引擎] 忽略未知事件类型 {type}。", null);
                    continue;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _process.StandardInput.WriteLineAsync("abort").ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // stdin may already be gone when the child exited; killing below covers it.
                }

                if (!_process.WaitForExit(2500))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke("Brother 引擎会话关闭时出现非致命异常。", ex);
        }
        finally
        {
            _process.Dispose();
        }
    }

    private int? WaitForExitSilently()
    {
        try
        {
            if (_process.WaitForExit(1000))
            {
                return _process.ExitCode;
            }
        }
        catch
        {
            // process already disposed
        }

        return null;
    }
}
