// Brother engine bridge (x86). The official ScanCoreApi.dll is 32-bit only, while the main
// ipscan app runs x64, so every engine call happens in this child process. Protocol:
//
//   stdin : one JSON line  {vid,pid,name,serial,dpi,color,duplex,feed,paper,deskew,pages,conn,ip,
//                           monoThreshold,difBrightness,difContrast,autoRotate}
//           plus a bare "abort" line to cancel.
//   stderr: human-readable diagnostics.
//   stdout: NDJSON events {"t":"page",enc:"bgra|jpeg",...} | {"t":"blank",...} | {"t":"done",pages} | {"t":"error",...}
//
// Modes: --probe (load + Initialize/UnInitialize) | --scan (full job).

using System.Text.Json;

namespace WatermarkRemover.Scan.Bridge;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "--scan";

        if (mode == "--probe")
        {
            var error = EngineScanner.Probe(out var modelName);
            if (error is null)
            {
                Console.Out.WriteLine("ok");
                return 0;
            }

            Console.Error.WriteLine(error);
            return 2;
        }

        if (mode == "--sizes")
        {
            // Struct-layout verification: these sizes must match the target's own decompiled
            // structs byte-for-byte (see research StructCheck probe).
            var names = new[]
            {
                "BS_SELECT_DEVICE_INFO", "BS_SCANNER_PARAMETERS", "BS_ADVANCEDSCAN", "SCANAREAINFO",
                "SCANAREA", "OUTPUT_FORMAT", "USERAUTHENTICATIONINFO", "BS_SCAN_IMAGEDATA",
                "BS_SCANNER_CAPABILITY", "BS_PUSHSCAN_PARAMETERS", "AUTOCONTINUOUSSCANPARAM",
                "COLORTONEPARAM", "COLORDROPPARAM", "SMOOTHINGPARAM", "REMOVELINEPARAM",
                "PUNCHHOLEPARAM", "EDGECUTPARAM", "EDGEFILLPARAM", "TEARFILLPARAM",
                "LOWSPEEDMODEPARAM", "CARDMODEPARAM", "BARCODEPARAMETER", "PATCHCODEPARAMETER",
                "COMBINEIMAGESPARAM", "ERRORDIFFUSIONTONEPARAM", "AUTOCOLORLEVELPARAM",
                "ADJUSTMENTPARAM", "BARCODE_OUTPUT", "BARCODE_INFO", "BS_SNMP_COMMUNITYNAME",
                "UsbId", "POSSIBLERANGE", "PARAMETERRANGE", "MARGINSIZE", "RECT",
            };
            foreach (var n in names)
            {
                var t = typeof(Interop.BrotherScanCore).GetNestedType(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (t is null) { Console.Out.WriteLine($"{n}: NOT FOUND"); continue; }
                try { Console.Out.WriteLine($"{n}: {System.Runtime.InteropServices.Marshal.SizeOf(t)}"); }
                catch (Exception ex) { Console.Out.WriteLine($"{n}: ERROR {ex.Message}"); }
            }

            return 0;
        }

        if (mode != "--scan")
        {
            Console.Error.WriteLine($"未知模式 {mode}，支持 --probe / --scan。");
            return 2;
        }

        ScanRequest request;
        try
        {
            var line = Console.In.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                Console.Error.WriteLine("stdin 未提供扫描请求。");
                return 2;
            }

            request = JsonSerializer.Deserialize<ScanRequest>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new JsonException("请求反序列化为空。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"解析请求失败：{ex.Message}");
            return 2;
        }

        using var abortSource = new CancellationTokenSource();
        _ = Task.Run(() => WatchAbort(abortSource));

        try
        {
            var error = EngineScanner.Run(request, Console.Out, abortSource.Token);
            if (error is not null)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { t = "error", msg = error }));
                Console.Error.WriteLine(error);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { t = "aborted" }));
            return 3;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { t = "error", msg = ex.ToString() }));
            return 1;
        }
    }

    private static void WatchAbort(CancellationTokenSource abortSource)
    {
        while (!abortSource.IsCancellationRequested)
        {
            var line = Console.In.ReadLine();
            if (line is null)
            {
                return; // stdin closed: parent exited; let the job finish or stall-guard handle it.
            }

            if (line.Trim().Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                Interop.BrotherScanCore.AbortScanning();
                abortSource.Cancel();
                return;
            }
        }
    }
}
