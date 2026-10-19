using System.Text.Json;
using System.Text.RegularExpressions;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine.BrotherEngine;

/// <summary>
/// Scans through the engine the target application itself uses: Brother's 32-bit ScanCoreApi.dll
/// (ADK), reached from this x64 app through the IpsClone.BrotherEngine bridge process. This is the
/// private channel that gives the target its capture speed; WIA remains the fallback when the
/// official application is not installed.
/// </summary>
public sealed partial class BrotherEngineScanSource : IScanSource
{
    private readonly Action<string, Exception?>? _trace;
    private bool? _channelPresent;

    public BrotherEngineScanSource(Action<string, Exception?>? trace = null) => _trace = trace;

    public string Id => "brother-engine";

    public string DisplayName => "Brother 引擎直连（ADK）";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        _channelPresent ??= BrotherEngineLocator.IsChannelPresent();
        if (!_channelPresent.Value)
        {
            _trace?.Invoke("Brother 引擎通道不可用：缺少桥接进程或官方 ScanCoreApi.dll，将回退 WIA。", null);
        }

        return Task.FromResult(_channelPresent.Value);
    }

    public Task<IReadOnlyList<ScanDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<ScanDeviceInfo>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Status = 'OK' AND PNPDeviceID LIKE 'USB\\\\VID_04F9%'");

            // A composite device surfaces as several entries: the usbccgp parent carries the real
            // USB serial but only the generic name "USB Composite Device", while the MI_xx
            // function children carry the model name ("Brother DCP-B7658DW") on container-id
            // instance ids. The target builds nameDevice from vendor+model and pins the device
            // with the UniqueID serial, so merge each VID/PID family: serial from the parent,
            // display name from the function entries.
            var families = new Dictionary<string, DeviceFamily>(StringComparer.OrdinalIgnoreCase);
            var order = new List<DeviceFamily>();

            foreach (var entity in searcher.Get())
            {
                var instanceId = entity["PNPDeviceID"] as string ?? string.Empty;
                var name = entity["Name"] as string ?? string.Empty;
                var match = DeviceIdPattern().Match(instanceId);
                if (!match.Success)
                {
                    continue;
                }

                var vid = match.Groups["vid"].Value;
                var pid = match.Groups["pid"].Value;
                var key = $"{vid}_{pid}";
                if (!families.TryGetValue(key, out var family))
                {
                    family = new DeviceFamily(vid, pid);
                    families[key] = family;
                    order.Add(family);
                }

                // …\VID_xxxx&PID_xxxx\SERIAL is the composite parent; the &MI_xx children end in
                // container ids ("6&2CEE6D59&1&0000") and carry no serial.
                var tail = instanceId[(match.Index + match.Length)..].TrimStart('\\');
                if (tail.Length > 0 && !tail.Contains('&') && !tail.Contains('\\'))
                {
                    if (family.Serial.Length == 0)
                    {
                        family.Serial = tail;
                        family.InstanceId = instanceId;
                    }
                }
                else if (family.InstanceId.Length == 0)
                {
                    family.InstanceId = instanceId;
                }

                family.NoteName(name);
            }

            foreach (var family in order)
            {
                devices.Add(new ScanDeviceInfo
                {
                    SourceId = Id,
                    DeviceId = family.InstanceId,
                    Name = family.Name.Length > 0 ? family.Name : "Brother 设备",
                    Manufacturer = "Brother",
                    Capabilities = new Dictionary<string, string>
                    {
                        ["brother.vid"] = family.Vid,
                        ["brother.pid"] = family.Pid,
                        ["brother.serial"] = family.Serial,
                        ["brother.instance"] = family.InstanceId,
                    },
                });
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke("Brother USB 设备枚举失败，返回空列表（WIA 仍可用）。", ex);
        }

        return Task.FromResult<IReadOnlyList<ScanDeviceInfo>>(devices);
    }

    public Task<IScanSession> OpenAsync(
        ScanDeviceInfo device,
        ScanSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);

        var helper = BrotherEngineLocator.FindHelperExecutable()
            ?? throw new InvalidOperationException("未找到 IpsClone.BrotherEngine 桥接进程。");
        var request = BuildRequest(device, settings);
        var session = new BrotherEngineSession(helper, request, device, _trace);
        session.Start(cancellationToken);
        return Task.FromResult<IScanSession>(session);
    }

    internal static object BuildRequest(ScanDeviceInfo device, ScanSettings settings)
    {
        device.Capabilities.TryGetValue("brother.vid", out var vidText);
        device.Capabilities.TryGetValue("brother.pid", out var pidText);
        var vid = int.TryParse(vidText, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0x04F9;
        var pid = int.TryParse(pidText, System.Globalization.NumberStyles.HexNumber, null, out var p) ? p : 0;

        // The bridge pins the USB device with the target's own UniqueID format
        // ("VVVV_PPPP_serial", see DeviceInfo.UniqueID in the target's common layer).
        return new
        {
            vid,
            pid,
            name = device.Name,
            serial = ExtractSerial(device),
            dpi = (int)settings.Resolution,
            color = MapColor(settings.Color),
            duplex = MapDuplex(settings.Duplex),
            feed = MapFeed(settings.Feed),
            paper = MapPaper(settings.Paper),
            deskew = settings.CorrectSkew,
            monoThreshold = settings.MonoThreshold,
            difBrightness = settings.DiffusionBrightness,
            difContrast = settings.DiffusionContrast,
            autoRotate = settings.AutoRotate,
            pages = 0, // the target's default ticket: no page cap, end when the feeder empties
            conn = "usb",
            ip = "",
        };
    }

    /// <summary>Pulls the serial segment out of a USB PnP instance id (…\VID_xxxx&amp;PID_xxxx\SERIAL).</summary>
    internal static string ExtractSerial(ScanDeviceInfo device)
    {
        if (device.Capabilities.TryGetValue("brother.serial", out var serial) &&
            !string.IsNullOrWhiteSpace(serial))
        {
            return serial;
        }

        var id = device.DeviceId;
        var separator = id.LastIndexOf('\\');
        var candidate = separator >= 0 ? id[(separator + 1)..] : string.Empty;
        // Interface children (&MI_00) carry container ids like "6&2A4F4E44&0&0000", not serials.
        return candidate.Contains('&') || candidate.Equals("ROOT", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : candidate;
    }

    internal static string MapColor(ColorMode color) => color switch
    {
        ColorMode.Auto => "AUTOCOLOR",
        ColorMode.Color24 => "COLOR24BIT",
        ColorMode.GrayDiffusion => "ERRDIFFUSION",
        ColorMode.TrueGray => "TRUEGRAY",
        ColorMode.BlackWhite => "BLACKWHITE",
        _ => "COLOR24BIT",
    };

    internal static string MapDuplex(DuplexMode duplex) => duplex switch
    {
        DuplexMode.LongEdge => "DUPLEX_LONG",
        DuplexMode.ShortEdge => "DUPLEX_SHORT",
        _ => "SIMPLEX",
    };

    internal static string MapFeed(FeedMode feed) => feed switch
    {
        FeedMode.Flatbed => "FLATBED",
        FeedMode.Adf => "ADF",
        _ => "SOURCEAUTO",
    };

    internal static string MapPaper(PaperSize paper) => paper switch
    {
        PaperSize.Auto => "AUTOSIZE",
        PaperSize.A3 => "A3",
        PaperSize.A4 => "A4",
        PaperSize.A5 => "A5",
        PaperSize.A6 => "A6",
        PaperSize.B4 => "JISB4",
        PaperSize.B5 => "JISB5",
        PaperSize.B6 => "JISB6",
        PaperSize.Letter => "LETTER",
        PaperSize.Legal => "LEGAL",
        PaperSize.Executive => "EXECUTIVE",
        PaperSize.Folio => "FOLIO",
        PaperSize.Ledger => "LEDGER",
        PaperSize.HalfLetter => "HALFLETTER",
        PaperSize.IndiaLegal => "INDIALEGAL",
        PaperSize.MexicoLegal => "MEXICANLEGAL",
        PaperSize.JapaneseHagaki => "POSTCARD",
        PaperSize.BusinessCard60x90 => "BCARD60X90",
        PaperSize.BusinessCard90x60 => "BCARD90X60",
        PaperSize.IndexCard13x20 => "INDEXCARD",
        PaperSize.Photo10x15 => "PHOTO",
        PaperSize.Photo13x18 => "PHOTOL",
        PaperSize.Photo9x13 => "PHOTO2L",
        PaperSize.LongPaperNarrowWidth => "LONGPAPER_NARROW",
        PaperSize.LongPaperNormalWidth => "LONGPAPER_WIDE",
        _ => "A4",
    };

    /// <summary>One physical device merged across its composite USB entries (see DiscoverAsync).</summary>
    private sealed class DeviceFamily(string vid, string pid)
    {
        public string Vid { get; } = vid;

        public string Pid { get; } = pid;

        /// <summary>USB serial string, taken from the composite parent entry.</summary>
        public string Serial { get; set; } = string.Empty;

        /// <summary>Parent instance id when present, otherwise the first function entry.</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>Best display name seen: a model name beats generic composite/port entries.</summary>
        public string Name { get; set; } = string.Empty;

        public void NoteName(string name)
        {
            if (name.Length == 0)
            {
                return;
            }

            if (IsModelName(name) || Name.Length == 0)
            {
                Name = name;
            }
        }

        private static bool IsModelName(string name) =>
            !name.Contains("Composite", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Port", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Hub", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})")]
    private static partial Regex DeviceIdPattern();
}
