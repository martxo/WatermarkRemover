using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Scan.Bridge.Interop;

namespace WatermarkRemover.Scan.Bridge;

/// <summary>One scan job as received over stdin (see docs in Program.cs).</summary>
internal sealed record ScanRequest(
    int Vid,
    int Pid,
    string Name,
    string Serial,
    int Dpi,
    string Color,
    string Duplex,
    string Feed,
    string Paper,
    bool Deskew,
    int Pages,
    string Conn = "usb",
    string Ip = "",
    int MonoThreshold = -1,
    int DifBrightness = 0,
    int DifContrast = 0,
    bool AutoRotate = false);

internal static class EngineScanner
{
    private const string BundleId = "bundleID"; // literal the target passes too
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Loads the native engine and verifies Initialize/UnInitialize round-trip.</summary>
    internal static string? Probe(out string modelName)
    {
        modelName = string.Empty;
        var engineDir = LocateEngineDirectory();
        if (engineDir is null)
        {
            return "未找到 ScanCoreApi.dll（需要安装 Brother iPrint&Scan）。";
        }

        if (!LoadEngine(engineDir))
        {
            return $"ScanCoreApi.dll 加载失败（目录 {engineDir}）：{DescribeLoadFailure()}";
        }

        var rc = BrotherScanCore.Initialize(BundleId, false);
        if (rc != 0)
        {
            return $"Initialize 失败：0x{rc:X8}";
        }

        BrotherScanCore.UnInitialize();
        return null;
    }

    /// <summary>Runs a full scan job, streaming NDJSON events to stdout. Returns the error text or null.</summary>
    internal static string? Run(ScanRequest request, TextWriter stdout, CancellationToken cancel)
    {
        var engineDir = LocateEngineDirectory();
        if (engineDir is null)
        {
            return "未找到 ScanCoreApi.dll（需要安装 Brother iPrint&Scan）。";
        }

        if (!LoadEngine(engineDir))
        {
            return $"ScanCoreApi.dll 加载失败（目录 {engineDir}）：{DescribeLoadFailure()}";
        }

        var rc = BrotherScanCore.Initialize(BundleId, false);
        if (rc != 0)
        {
            return $"Initialize 失败：0x{rc:X8}";
        }

        try
        {
            // Transcribed from the target's ScanCoreApi.ConvertDeviceInfo(DeviceInfo):
            // USB puts the UniqueID ("VVVV_PPPP_serial") into addrNetwork, the engine uses it
            // to pin the exact device instance; network passes the bare model name + IP.
            BrotherScanCore.BS_SELECT_DEVICE_INFO device;
            if (request.Conn.Equals("net", StringComparison.OrdinalIgnoreCase))
            {
                device = new BrotherScanCore.BS_SELECT_DEVICE_INFO
                {
                    typeDeviceIf = BrotherScanCore.devif_type.DEVIF_TYPE_NETWORK,
                    typeDeviceId = BrotherScanCore.devid_type.DEVID_TYPE_NETADDR,
                    nameDevice = Truncate(request.Name, 127),
                    addrNetwork = Truncate(request.Ip, 127),
                    idLocation = 0u,
                };
            }
            else
            {
                var name = request.Name.StartsWith("Brother", StringComparison.InvariantCultureIgnoreCase)
                    ? request.Name
                    : "Brother " + request.Name;
                var uniqueId = $"{request.Vid:X4}_{request.Pid:X4}_{request.Serial}";
                Log($"USB UniqueID：{uniqueId}");
                device = new BrotherScanCore.BS_SELECT_DEVICE_INFO
                {
                    typeDeviceIf = BrotherScanCore.devif_type.DEVIF_TYPE_USB,
                    typeDeviceId = BrotherScanCore.devid_type.DEVID_TYPE_USBID,
                    nameDevice = Truncate(name, 127),
                    addrNetwork = Truncate(uniqueId, 127),
                    usbId = new BrotherScanCore.UsbId
                    {
                        idVendor = checked((ushort)request.Vid),
                        idProduct = checked((ushort)request.Pid),
                    },
                    idLocation = 0u,
                };
            }
            rc = BrotherScanCore.SelectDevice(ref device);
            if (rc != 0)
            {
                return $"SelectDevice 失败：0x{rc:X8}";
            }

            Log($"设备已选择：{request.Name} (VID_{request.Vid:X4}&PID_{request.Pid:X4})");

            // Scan-time flow of the target (ScanCoreService): SelectDevice → (ADF source only:
            // managed QueryAdfState, which internally does caps+adf) → StartScanning. The
            // capability re-init round-trip and GetPushScanParameter belong to device-selection
            // / push-scan paths, NOT the pull-scan session — calling them here derails the
            // session (device reports a cancel at first ReadImageData).
            if (request.Feed.Equals("ADF", StringComparison.OrdinalIgnoreCase))
            {
                var scratch = Marshal.AllocHGlobal(64 * 1024);
                try
                {
                    BrotherScanCore.QueryDeviceCapabilities(scratch);
                }
                finally
                {
                    Marshal.FreeHGlobal(scratch);
                }

                var adf = BrotherScanCore.QueryAdfState();
                Log($"ADF 状态：{adf}");
            }

            var parameters = BuildParameters(request);
            rc = BrotherScanCore.StartScanning(ref parameters);
            if (rc != 0)
            {
                return $"StartScanning 失败：0x{rc:X8}（{DescribeError(rc)}）";
            }

            return RunReceiveLoop(stdout, request, cancel);
        }
        finally
        {
            BrotherScanCore.UnInitialize();
        }
    }

    /// <summary>The page receive loop, mirroring the target's ScanCoreService.Scan status machine.</summary>
    private static string? RunReceiveLoop(TextWriter stdout, ScanRequest request, CancellationToken cancel)
    {
        var lastChange = DateTime.UtcNow;
        var previous = (BrotherScanCore.ScanApiStatus)(-1);
        var page = 0;
        var autoContinuous = false;

        while (true)
        {
            cancel.ThrowIfCancellationRequested();

            var status = BrotherScanCore.GetScanStatus();
            if (status != previous)
            {
                Log($"状态：{status}");
                lastChange = DateTime.UtcNow;
                previous = status;
            }
            else if (DateTime.UtcNow - lastChange > StallTimeout)
            {
                BrotherScanCore.AbortScanning();
                return $"引擎状态 {status} 停滞超过 {StallTimeout.TotalSeconds:F0} 秒，已中止。";
            }

            switch (status)
            {
                case BrotherScanCore.ScanApiStatus.XferReady:
                {
                    var error = ReadAndEmitPage(stdout, page);
                    if (error is not null)
                    {
                        return error;
                    }

                    page++;
                    lastChange = DateTime.UtcNow;
                    break;
                }

                case BrotherScanCore.ScanApiStatus.AllEnd:
                    Emit(stdout, new { t = "done", pages = page });
                    if (autoContinuous)
                    {
                        BrotherScanCore.FinishAutoContinuousScan();
                    }

                    return null;

                case BrotherScanCore.ScanApiStatus.StandBy:
                case BrotherScanCore.ScanApiStatus.WaitingNextPage:
                case BrotherScanCore.ScanApiStatus.Receiving:
                case BrotherScanCore.ScanApiStatus.WaitingDocumentInsertion:
                    // The target's poll loop has no sleep — GetScanStatus drives the engine's
                    // internal pump, so keep calling it back-to-back exactly like the target.
                    break;

                default:
                    return $"未知引擎状态 {(int)status}。";
            }
        }
    }

    private static string? ReadAndEmitPage(TextWriter stdout, int page)
    {
        var data = new BrotherScanCore.BS_SCAN_IMAGEDATA();
        int pageNo = page;
        var rc = BrotherScanCore.ReadImageData(ref data, ref pageNo);
        if (data.isBlankPage)
        {
            Emit(stdout, new { t = "blank", n = page });
            BrotherScanCore.FreeImageData(pageNo);
            return null;
        }

        if (rc != 0)
        {
            Log($"ReadImageData rc=0x{rc:X8} 字段转储：dataSize={data.dataSize} ptr=0x{data.pDataPtr.ToInt64():X} " +
                $"{data.width}x{data.height} {data.bpp}bpp stride={data.widthbytes} blank={data.isBlankPage} " +
                $"jpeg={data.isJpegCompression} terminated={data.isScanTerminated} paperSource={data.paperSource}");
            if (data.dataSize > 0 && data.pDataPtr != IntPtr.Zero && !data.isBlankPage && !data.isJpegCompression
                && data.width > 0 && data.height > 0)
            {
                Log("rc 非零但数据有效，尝试按数据继续。");
                var raw2 = new byte[data.dataSize];
                Marshal.Copy(data.pDataPtr, raw2, 0, raw2.Length);
                BrotherScanCore.FreeImageData(pageNo);
                byte[] bgra2;
                try
                {
                    bgra2 = ToBgra(raw2, data.width, data.height, data.bpp, data.widthbytes);
                }
                catch (Exception ex)
                {
                    return $"第 {pageNo} 页位图转换失败（{data.width}x{data.height} {data.bpp}bpp stride={data.widthbytes}）：{ex.Message}";
                }

                Log($"第 {pageNo} 页（rc=0x{rc:X8} 携带）：{data.width}x{data.height} {data.bpp}bpp {data.resolutionX}dpi");
                EmitPage(stdout, page, data, "bgra", bgra2);
                return null;
            }

            return $"ReadImageData（第 {pageNo} 页）失败：0x{rc:X8}（{DescribeError(rc)}）";
        }

        if (data.isJpegCompression)
        {
            // JPEG pages stream through untouched; the parent decodes them with Skia
            // (BrotherEngineSession). Transfer stays small (~1 MB vs ~46 MB of BGRA).
            var jpeg = new byte[data.dataSize];
            Marshal.Copy(data.pDataPtr, jpeg, 0, jpeg.Length);
            BrotherScanCore.FreeImageData(pageNo);
            Log($"第 {pageNo} 页：JPEG 压缩数据 {jpeg.Length / 1024.0:F0} KB（透传至父进程解码）。");
            EmitPage(stdout, page, data, "jpeg", jpeg);
            return null;
        }

        var raw = new byte[data.dataSize];
        Marshal.Copy(data.pDataPtr, raw, 0, raw.Length);
        BrotherScanCore.FreeImageData(pageNo);

        byte[] bgra;
        try
        {
            bgra = ToBgra(raw, data.width, data.height, data.bpp, data.widthbytes);
        }
        catch (Exception ex)
        {
            return $"第 {pageNo} 页位图转换失败（{data.width}x{data.height} {data.bpp}bpp stride={data.widthbytes}）：{ex.Message}";
        }

        Log($"第 {pageNo} 页：{data.width}x{data.height} {data.bpp}bpp {data.resolutionX}dpi，BGRA {bgra.Length / 1024.0:F0} KB");
        EmitPage(stdout, page, data, "bgra", bgra);
        return null;
    }

    private static void EmitPage(TextWriter stdout, int page, BrotherScanCore.BS_SCAN_IMAGEDATA data, string enc, byte[] payload) =>
        Emit(stdout, new
        {
            t = "page",
            n = page,
            w = data.width,
            h = data.height,
            dpiX = data.resolutionX,
            dpiY = data.resolutionY,
            enc,
            b64 = Convert.ToBase64String(payload),
        });

    /// <summary>Expands the engine's raw scanlines into the 32-bit BGRA layout RasterImage expects.</summary>
    internal static byte[] ToBgra(byte[] raw, int width, int height, int bpp, int stride)
    {
        var output = new byte[checked(width * height * 4)];
        switch (bpp)
        {
            case 1:
            {
                // Packed monochrome, MSB first; 1 = white, 0 = black (Windows convention).
                for (var y = 0; y < height; y++)
                {
                    var rowStart = checked(y * stride);
                    var outRow = checked(y * width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var bit = (raw[rowStart + (x >> 3)] >> (7 - (x & 7))) & 1;
                        var value = (byte)(bit == 1 ? 0xFF : 0x00);
                        var o = outRow + (x * 4);
                        output[o] = value;
                        output[o + 1] = value;
                        output[o + 2] = value;
                        output[o + 3] = 0xFF;
                    }
                }

                return output;
            }

            case 8:
            {
                for (var y = 0; y < height; y++)
                {
                    var rowStart = checked(y * stride);
                    var outRow = checked(y * width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var g = raw[rowStart + x];
                        var o = outRow + (x * 4);
                        output[o] = g;
                        output[o + 1] = g;
                        output[o + 2] = g;
                        output[o + 3] = 0xFF;
                    }
                }

                return output;
            }

            case 24:
            {
                for (var y = 0; y < height; y++)
                {
                    var rowStart = checked(y * stride);
                    var outRow = checked(y * width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var s = rowStart + (x * 3);
                        var o = outRow + (x * 4);
                        output[o] = raw[s];       // B
                        output[o + 1] = raw[s + 1]; // G
                        output[o + 2] = raw[s + 2]; // R
                        output[o + 3] = 0xFF;
                    }
                }

                return output;
            }

            default:
                throw new NotSupportedException($"不支持的位深 {bpp}（支持 1/8/24）。");
        }
    }

    private static BrotherScanCore.BS_SCANNER_PARAMETERS BuildParameters(ScanRequest request)
    {
        var parameters = new BrotherScanCore.BS_SCANNER_PARAMETERS
        {
            resolutionX = checked((uint)request.Dpi),
            resolutionY = checked((uint)request.Dpi),
            colortype = MapColor(request.Color),
            duplextype = MapDuplex(request.Duplex),
            paperSource = MapFeed(request.Feed),
            scanAreaInfo = BuildScanArea(request),
            appType = BrotherScanCore.APPTYPE.UNKNOWN_APP,
        };
        parameters.enhanced.deskew = request.Deskew;
        // Advanced parameters, mirroring the target's GetAdvancedParameters pass-through.
        // monoThreshold only takes effect for BLACKWHITE and diffusionTone only for
        // ERRDIFFUSION (official semantics); the other colour modes ignore both.
        if (request.MonoThreshold >= 0)
        {
            parameters.enhanced.monoThreshold = request.MonoThreshold;
        }

        if (request.DifBrightness != 0 || request.DifContrast != 0)
        {
            parameters.enhanced.diffusionTone = new BrotherScanCore.ERRORDIFFUSIONTONEPARAM
            {
                brightness = request.DifBrightness,
                contrast = request.DifContrast,
            };
        }

        // The target ships SetAutoRotate off by default; only forward an explicit opt-in.
        parameters.enhanced.autoRotate = request.AutoRotate;
        // Official semantics (GetAdvancedParameters): xferCount always copies ticket.ScanPages,
        // and when ScanPages==0 the auto-continuous fields copy the AdvancedOption defaults —
        // execute=false, timeout=10, feedDelay=1. The UI never sets ScanPages, so a pull scan
        // genuinely runs with xferCount=0 ("no page cap — end when the feeder empties").
        // execute=true is reserved for the device's 连续扫描 feature, not ordinary batches.
        if (request.Pages > 0)
        {
            parameters.enhanced.xferCount = request.Pages;
        }
        else
        {
            parameters.enhanced.xferCount = 0;
            parameters.enhanced.autoContinuousScan.execute = false;
            parameters.enhanced.autoContinuousScan.timeout = 10;
            parameters.enhanced.autoContinuousScan.feedDelay = 1;
        }
        // The target always passes byte buffers here (all-zero when there is no authentication);
        // a null array would marshal to a NULL pointer inside the native parameter block.
        parameters.userAuthenticationInfo.userName = new byte[513];
        parameters.userAuthenticationInfo.userPassword = new byte[33];
        return parameters;
    }

    /// <summary>
    /// Paper-type mapping per the target's GetScanAreaInfo semantics. PAPERTYPE.AUTOSIZE is set
    /// exclusively by the push-scan path (device-panel scans where the device dictates the
    /// parameters via GetPushScanParameter); the target's pull-scan tickets default to ISO_A4
    /// and its UI never sets AdvancedSetting.AutoSize. Both AUTOSIZE+FLATBED and
    /// AUTOSIZE+ADF+duplex derail the engine's image pipeline (0x10403003), confirming that
    /// pull scans must carry a concrete size. "Auto" therefore degrades to A4, the target's
    /// own ticket default.
    /// </summary>
    private static BrotherScanCore.SCANAREAINFO BuildScanArea(ScanRequest request)
    {
        if (request.Paper.Equals("AUTOSIZE", StringComparison.OrdinalIgnoreCase))
        {
            Log("AUTOSIZE 为引擎推送扫描专用取值，拉扫描按官方默认回退 A4。");
            return new BrotherScanCore.SCANAREAINFO
            {
                papertype = BrotherScanCore.PAPERTYPE.A4,
                areaSelected = false,
            };
        }

        return new BrotherScanCore.SCANAREAINFO
        {
            papertype = MapPaper(request.Paper),
            areaSelected = false,
        };
    }

    internal static BrotherScanCore.COLORTYPE MapColor(string color) => color.ToUpperInvariant() switch
    {
        "AUTOCOLOR" => BrotherScanCore.COLORTYPE.AUTOCOLOR,
        "BLACKWHITE" => BrotherScanCore.COLORTYPE.BLACKWHITE,
        "ERRDIFFUSION" => BrotherScanCore.COLORTYPE.ERRDIFFUSION,
        "TRUEGRAY" => BrotherScanCore.COLORTYPE.TRUEGRAY,
        "COLOR24BIT" => BrotherScanCore.COLORTYPE.COLOR24BIT,
        _ => BrotherScanCore.COLORTYPE.COLOR24BIT,
    };

    internal static BrotherScanCore.DUPLEXTYPE MapDuplex(string duplex) => duplex.ToUpperInvariant() switch
    {
        "SIMPLEX" => BrotherScanCore.DUPLEXTYPE.SIMPLEX,
        "DUPLEX_LONG" => BrotherScanCore.DUPLEXTYPE.DUPLEX_LONG,
        "DUPLEX_SHORT" => BrotherScanCore.DUPLEXTYPE.DUPLEX_SHORT,
        _ => BrotherScanCore.DUPLEXTYPE.SIMPLEX,
    };

    internal static BrotherScanCore.PAPERSOURCESELECT MapFeed(string feed) => feed.ToUpperInvariant() switch
    {
        "SOURCEAUTO" => BrotherScanCore.PAPERSOURCESELECT.SOURCEAUTO,
        "FLATBED" => BrotherScanCore.PAPERSOURCESELECT.FLATBED,
        "ADF" => BrotherScanCore.PAPERSOURCESELECT.ADF,
        _ => BrotherScanCore.PAPERSOURCESELECT.SOURCEAUTO,
    };

    internal static BrotherScanCore.PAPERTYPE MapPaper(string paper) => paper.ToUpperInvariant() switch
    {
        "AUTOSIZE" => BrotherScanCore.PAPERTYPE.AUTOSIZE,
        "A4" => BrotherScanCore.PAPERTYPE.A4,
        "LETTER" => BrotherScanCore.PAPERTYPE.LETTER,
        "LEGAL" => BrotherScanCore.PAPERTYPE.LEGAL,
        "A3" => BrotherScanCore.PAPERTYPE.A3,
        "LEDGER" => BrotherScanCore.PAPERTYPE.LEDGER,
        "JISB4" => BrotherScanCore.PAPERTYPE.JISB4,
        "JISB5" => BrotherScanCore.PAPERTYPE.JISB5,
        "A5" => BrotherScanCore.PAPERTYPE.A5,
        "HALFLETTER" => BrotherScanCore.PAPERTYPE.HALFLETTER,
        "JISB6" => BrotherScanCore.PAPERTYPE.JISB6,
        "A6" => BrotherScanCore.PAPERTYPE.A6,
        "EXECUTIVE" => BrotherScanCore.PAPERTYPE.EXECUTIVE,
        "FOLIO" => BrotherScanCore.PAPERTYPE.FOLIO,
        "MEXICANLEGAL" => BrotherScanCore.PAPERTYPE.MEXICANLEGAL,
        "INDIALEGAL" => BrotherScanCore.PAPERTYPE.INDIALEGAL,
        "LONGPAPER_NARROW" => BrotherScanCore.PAPERTYPE.LONGPAPER_NARROW,
        "LONGPAPER_WIDE" => BrotherScanCore.PAPERTYPE.LONGPAPER_WIDE,
        "POSTCARD" => BrotherScanCore.PAPERTYPE.POSTCARD,
        "POSTCARD2" => BrotherScanCore.PAPERTYPE.POSTCARD2,
        "BCARD90X60" => BrotherScanCore.PAPERTYPE.BCARD90X60,
        "BCARD60X90" => BrotherScanCore.PAPERTYPE.BCARD60X90,
        "INDEXCARD" => BrotherScanCore.PAPERTYPE.INDEXCARD,
        "PHOTO" => BrotherScanCore.PAPERTYPE.PHOTO,
        "PHOTOL" => BrotherScanCore.PAPERTYPE.PHOTOL,
        "PHOTO2L" => BrotherScanCore.PAPERTYPE.PHOTO2L,
        _ => BrotherScanCore.PAPERTYPE.A4,
    };

    /// <summary>Resolves the official install directory that ships ScanCoreApi.dll.</summary>
    internal static string? LocateEngineDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
        };
        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var dir = Path.Combine(root, "Brother", "iPrint&Scan", "ScanCoreApi.dll");
            if (File.Exists(dir))
            {
                return Path.GetDirectoryName(dir);
            }
        }

        return null;
    }

    private static bool LoadEngine(string engineDirectory)
    {
        // The official app's exe lives in the engine folder, so bare-name LoadLibrary calls
        // made by the engine at scan time (BsAdditionalImageProcessing.dll etc.) resolve
        // there naturally; replicate that by pointing the loader's search slot at the folder.
        if (!BrotherScanCore.SetDllDirectory(engineDirectory))
        {
            Log($"SetDllDirectory 失败：Win32 错误 {Marshal.GetLastWin32Error()}");
        }

        // LOAD_WITH_ALTERED_SEARCH_PATH makes statically imported Bs*.dll libraries resolve
        // from the engine's own folder, which is exactly how the official application loads it.
        var fullPath = Path.Combine(engineDirectory, "ScanCoreApi.dll");
        return BrotherScanCore.LoadLibraryEx(fullPath, IntPtr.Zero, BrotherScanCore.LOAD_WITH_ALTERED_SEARCH_PATH) != IntPtr.Zero;
    }

    /// <summary>Reports why LoadLibrary failed, resolving the usual Win32 loader codes.</summary>
    internal static string DescribeLoadFailure() => Marshal.GetLastWin32Error() switch
    {
        126 => "找不到模块或其依赖（LOAD_WITH_ALTERED_SEARCH_PATH 尝试均失败）",
        127 => "已找到模块但缺少导出函数",
        1114 => "DLL 初始化例程失败",
        193 => "模块位数与宿主不符",
        var code => $"Win32 错误 {code}",
    };

    private static string DescribeError(uint code)
    {
        // Result-code families from the target's dictionaries (BSRSLT_* in ScanCore.NetStandard).
        return code switch
        {
            0x1000000 => "已取消",
            0x1000001 => "命令已取消",
            0x1000002 => "超时取消",
            0x1000010 => "设备侧取消（STOP 键/设备中断）",
            _ => (code & 0xFF000000u) switch
            {
                0x50000000 => $"设备错误 {code & 0xFFFFFF}",
                0x40000000 => "设备无响应/连接失败",
                0x10000000 => $"其他错误 {code & 0xFFFFFF}",
                0x20000000 => "引擎库缺失",
                0x30000000 => "内存分配失败",
                0x60000000 => "命令响应无效",
                0x70000000 => $"参数无效 {code & 0xFFFFFF}",
                0x80000000 => "调用顺序无效",
                _ => $"模块 0x{(code >> 24):X2} 错误 0x{code & 0xFFFFFF:X6}",
            },
        };
    }

    private static void Emit(TextWriter stdout, object payload)
    {
        stdout.WriteLine(JsonSerializer.Serialize(payload));
        stdout.Flush();
    }

    private static void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);
}
