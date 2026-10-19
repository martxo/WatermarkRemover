using System.Text;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Scans through the WIA / STI stack (device class {6BDD1FC6-810F-11D0-BEC7-08002BE2092F}), the same
/// channel the target application uses. COM is reached by late binding only, so the prototype has no
/// interop assembly dependency and still behaves sensibly when no device is currently online.
/// </summary>
public sealed class WiaScanSource : IScanSource
{
    private readonly Action<string, Exception?>? _trace;

    /// <param name="trace">Optional diagnostic sink; property-level failures are reported here.</param>
    public WiaScanSource(Action<string, Exception?>? trace = null) => _trace = trace;

    public string Id => "wia";

    public string DisplayName => "WIA / STI 扫描仪";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        object? manager = null;
        try
        {
            manager = WiaInterop.CreateDeviceManager();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _trace?.Invoke("无法创建 WIA.DeviceManager（WIA 服务可能未安装或未注册）。", ex);
            return Task.FromResult(false);
        }
        finally
        {
            WiaInterop.TryRelease(ref manager);
        }
    }

    public Task<IReadOnlyList<ScanDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<ScanDeviceInfo>();
        object? manager = null;
        try
        {
            manager = WiaInterop.CreateDeviceManager();
            dynamic managerObject = manager;
            dynamic infoCollection = managerObject.DeviceInfos;

            var count = WiaInterop.SafeCount((object)infoCollection);
            if (count == 0)
            {
                _trace?.Invoke("WIA 枚举到 0 个设备（设备可能未开机/未连接，属正常情况）。", null);
                return Task.FromResult<IReadOnlyList<ScanDeviceInfo>>(devices);
            }

            for (var i = 1; i <= count; i++) // WIA collections are 1-based.
            {
                try
                {
                    dynamic info = infoCollection[i];
                    var deviceType = Convert.ToInt32(info.Type);
                    if (deviceType != WiaInterop.DeviceTypeScanner)
                    {
                        continue;
                    }

                    object idValue = info.DeviceID;
                    var deviceId = idValue?.ToString() ?? string.Empty;
                    var name = WiaInterop.TryReadPropertyValue((object)info, "Name") ?? "未命名扫描仪";
                    var manufacturer = WiaInterop.TryReadPropertyValue((object)info, "Manufacturer") ?? string.Empty;

                    var capabilities = WiaInterop.CaptureDeviceInfoProperties((object)info);
                    capabilities["wia.deviceType"] = "Scanner";

                    devices.Add(new ScanDeviceInfo
                    {
                        SourceId = Id,
                        DeviceId = deviceId,
                        Name = name,
                        Manufacturer = manufacturer,
                        Capabilities = capabilities,
                    });
                }
                catch (Exception ex)
                {
                    _trace?.Invoke($"枚举第 {i} 个 WIA 设备失败，已跳过。", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke("WIA 设备枚举失败，返回空列表。", ex);
        }
        finally
        {
            WiaInterop.TryRelease(ref manager);
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

        object? manager = null;
        object? connectedDevice = null;
        object? item = null;
        try
        {
            manager = WiaInterop.CreateDeviceManager();
            dynamic managerObject = manager;
            dynamic infoCollection = managerObject.DeviceInfos;
            var count = WiaInterop.SafeCount((object)infoCollection);

            object? match = null;
            object? firstScanner = null;
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    dynamic info = infoCollection[i];
                    if (Convert.ToInt32(info.Type) != WiaInterop.DeviceTypeScanner)
                    {
                        continue;
                    }

                    firstScanner ??= (object)info;
                    object idValue = info.DeviceID;
                    if (string.Equals(idValue?.ToString(), device.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        match = (object)info;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _trace?.Invoke($"匹配第 {i} 个 WIA 设备失败，已跳过。", ex);
                }
            }

            var target = match ?? firstScanner;
            if (target is null)
            {
                throw new InvalidOperationException(
                    $"未在 WIA 中找到设备「{device.Name}」（{device.DeviceId}）。设备可能离线或驱动未就绪。");
            }

            dynamic targetInfo = target;
            connectedDevice = targetInfo.Connect();

            dynamic deviceObject = connectedDevice;
            dynamic items = deviceObject.Items;
            item = items[1]; // WIA item tree is 1-based; item 1 is the root scan item.

            var session = new WiaScanSession(connectedDevice, item, device, settings, _trace);
            connectedDevice = null; // Ownership has moved to the session.
            item = null;
            return Task.FromResult<IScanSession>(session);
        }
        catch
        {
            WiaInterop.TryRelease(ref item);
            WiaInterop.TryRelease(ref connectedDevice);
            throw;
        }
        finally
        {
            WiaInterop.TryRelease(ref manager);
        }
    }

    /// <summary>
    /// Human readable probe of the WIA stack: availability, enumerated devices and, when a device can
    /// be opened, which scan properties it accepted. Chinese on purpose (shown to the user).
    /// </summary>
    public string DescribeCapabilities()
    {
        var report = new StringBuilder();
        report.AppendLine($"扫描来源：{Id}（{DisplayName}）");

        var available = IsAvailableAsync().GetAwaiter().GetResult();
        report.AppendLine($"WIA 服务可用：{(available ? "是" : "否（无法创建 WIA.DeviceManager）")}");
        if (!available)
        {
            return report.ToString();
        }

        var devices = DiscoverAsync().GetAwaiter().GetResult();
        report.AppendLine($"枚举到设备数：{devices.Count}");
        if (devices.Count == 0)
        {
            report.AppendLine("（未发现设备；设备离线或驱动未就绪时属正常结果）");
            return report.ToString();
        }

        foreach (var device in devices)
        {
            report.AppendLine($"- {device.Name}｜厂商：{device.Manufacturer}｜DeviceID：{device.DeviceId}");
            foreach (var capability in device.Capabilities)
            {
                report.AppendLine($"    能力 {capability.Key} = {capability.Value}");
            }

            try
            {
                var session = (WiaScanSession)OpenAsync(device, new ScanSettings()).GetAwaiter().GetResult();
                try
                {
                    foreach (var note in session.DiagnosticNotes)
                    {
                        report.AppendLine($"    {note}");
                    }
                }
                finally
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"    打开设备失败：{ex.Message}");
            }
        }

        return report.ToString();
    }
}
