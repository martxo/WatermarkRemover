using System.IO;
using System.Runtime.InteropServices;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Low level WIA / STI plumbing. Everything reaches COM through late binding
/// (<c>Type.GetTypeFromProgID</c> + <c>dynamic</c>), so this project needs no interop assemblies and
/// no compile-time reference to the WIA type library.
/// </summary>
internal static class WiaInterop
{
    /// <summary>ProgID of the WIA device manager.</summary>
    public const string DeviceManagerProgId = "WIA.DeviceManager";

    /// <summary>WIA device type that identifies a scanner (WIA_DEVICE_TYPE_SCANNER).</summary>
    public const int DeviceTypeScanner = 1;

    /// <summary>WIA_FORMAT_PNG image format guid accepted by IItem::Transfer.</summary>
    public const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

    // --- WIA item property ids (decimal ids from the WIA property table) -------------------------
    public const string IpsCurIntent = "6146";              // WIA_IPS_CUR_INTENT
    public const string IpsXRes = "6147";                   // WIA_IPS_XRES
    public const string IpsYRes = "6148";                   // WIA_IPS_YRES
    public const string IpsXPos = "6149";                   // WIA_IPS_XPOS
    public const string IpsYPos = "6150";                   // WIA_IPS_YPOS
    public const string IpsXExtent = "6151";                // WIA_IPS_XEXTENT
    public const string IpsYExtent = "6152";                // WIA_IPS_YEXTENT
    public const string IpaDataType = "4103";               // WIA_IPA_DATATYPE
    // WIA_DPS_DOCUMENT_HANDLING_* live on the DEVICE ROOT item, not on the scan item. Verified on the
    // Brother DCP-B7658DW: setting SELECT on the scan item fails with "无法找到 ID" while the device
    // root accepts it.
    public const string DpsDocumentHandlingSelect = "3088"; // WIA_DPS_DOCUMENT_HANDLING_SELECT
    // 3087 is STATUS; the neighbouring 3086 is CAPABILITIES and was used here by mistake before, which
    // made an empty feeder look permanently "ready".
    public const string DpsDocumentHandlingStatus = "3087"; // WIA_DPS_DOCUMENT_HANDLING_STATUS

    // --- WIA_IPS_CUR_INTENT bits (only the documented subset is ever set) ------------------------
    public const int IntentColor = 0x00000001;           // WIA_INTENT_IMAGE_TYPE_COLOR
    public const int IntentGrayScale = 0x00000002;       // WIA_INTENT_IMAGE_TYPE_GRAYSCALE
    public const int IntentText = 0x00000004;            // WIA_INTENT_IMAGE_TYPE_TEXT
    public const int IntentMaximizeQuality = 0x00020000; // WIA_INTENT_MAXIMIZE_QUALITY

    // --- WIA_IPA_DATATYPE values -----------------------------------------------------------------
    public const int DataThreshold = 0; // WIA_DATA_THRESHOLD (black and white)
    public const int DataDither = 1;    // WIA_DATA_DITHER (error diffusion grey)
    public const int DataGrayScale = 2; // WIA_DATA_GRAYSCALE
    public const int DataColor = 3;     // WIA_DATA_COLOR

    // --- WIA_DPS_DOCUMENT_HANDLING_SELECT bits ---------------------------------------------------
    // The device reports exactly these three bits as its capabilities (DCP-B7658DW: 3086 = 7), so there
    // is no "auto feed" bit to send; Auto is resolved by the caller instead.
    public const int HandlingFeeder = 0x001;   // FEEDER
    public const int HandlingFlatbed = 0x002;  // FLATBED
    public const int HandlingDuplex = 0x004;   // DUPLEX (only valid together with the feeder)

    // --- WIA_DPS_DOCUMENT_HANDLING_STATUS bits ---------------------------------------------------
    public const int StatusFeedReady = 0x001; // FEED_READY
    public const int StatusFlatReady = 0x002; // FLAT_READY
    public const int StatusDupReady = 0x004;  // DUP_READY

    /// <summary>Creates the WIA device manager COM object.</summary>
    public static object CreateDeviceManager()
    {
        var type = Type.GetTypeFromProgID(DeviceManagerProgId);
        if (type is null)
        {
            throw new InvalidOperationException($"ProgID「{DeviceManagerProgId}」未注册（WIA 服务可能未安装）。");
        }

        return Activator.CreateInstance(type)!;
    }

    /// <summary>Reads a COM collection count, returning 0 when the collection cannot be queried.</summary>
    public static int SafeCount(object collection)
    {
        try
        {
            dynamic dynamicCollection = collection;
            return Convert.ToInt32(dynamicCollection.Count);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Best-effort COM release; COM teardown failures must never surface to the caller.</summary>
    public static void TryRelease(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // Releasing is opportunistic: the GC owns whatever is left.
        }
    }

    /// <summary>Best-effort COM release that also clears the field/local it came from.</summary>
    public static void TryRelease(ref object? comObject)
    {
        var target = comObject;
        comObject = null;
        TryRelease(target);
    }

    /// <summary>Reads a property's text value; null when the property is missing or unreadable.</summary>
    public static string? TryReadPropertyValue(object comObject, string propertyId)
    {
        try
        {
            dynamic obj = comObject;
            object value = obj.Properties[propertyId].Value;
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads an integer property value; -1 when the property is missing or unreadable.</summary>
    public static int TryReadPropertyInt(object comObject, string propertyId)
    {
        try
        {
            dynamic obj = comObject;
            object value = obj.Properties[propertyId].Value;
            return Convert.ToInt32(value);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Sets a WIA property. Drivers reject unsupported properties with a COM exception; that is
    /// reported and swallowed, so one unsupported property never fails the whole scan.
    /// </summary>
    public static bool TrySetProperty(
        object comObject,
        string propertyId,
        object value,
        Action<string, Exception?>? trace,
        List<string> notes)
    {
        try
        {
            dynamic obj = comObject;
            obj.Properties[propertyId].Value = value;
            notes.Add($"已设置 WIA 属性 {propertyId} = {value}");
            trace?.Invoke($"WIA 属性 {propertyId} 设置为 {value}。", null);
            return true;
        }
        catch (Exception ex)
        {
            notes.Add($"设置 WIA 属性 {propertyId} = {value} 失败（设备可能不支持该项）：{ex.Message}");
            trace?.Invoke($"设备不支持 WIA 属性 {propertyId}（值 {value}），已跳过。", ex);
            return false;
        }
    }

    /// <summary>Captures the name/value pairs a WIA device info object exposes, for diagnostics.</summary>
    public static Dictionary<string, string> CaptureDeviceInfoProperties(object deviceInfo)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic info = deviceInfo;
            dynamic properties = info.Properties;
            var count = SafeCount((object)properties);
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    dynamic property = properties[i];
                    object nameValue = property.Name;
                    var name = nameValue?.ToString();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    object value = property.Value;
                    result[name] = value?.ToString() ?? string.Empty;
                }
                catch
                {
                    // Individual unreadable properties are simply not reported.
                }
            }
        }
        catch
        {
            // A device info without a readable property set still yields a usable ScanDeviceInfo.
        }

        return result;
    }

    /// <summary>
    /// Runs <c>IItem::Transfer</c> and returns the encoded image bytes.
    /// </summary>
    /// <remarks>
    /// The automation object returned by <c>Transfer</c> exposes its payload through
    /// <c>IImageFile.FileData</c>, which is NOT an <c>IStream</c> the CLR can cast to - on real hardware
    /// it surfaces as a raw COM object and every cast attempt fails. The reliable route is
    /// <c>IImageFile.SaveFile</c> into a temporary file, which we then read back and delete.
    /// Note that drivers are free to answer with their own native format: the Brother DCP-B7658DW
    /// returns an uncompressed 24bpp BMP even when PNG is requested, so the caller must decode by
    /// content rather than trusting the requested format.
    /// </remarks>
    public static byte[] TransferImageBytes(object item, string formatId)
    {
        object? imageFile = null;
        string? tempPath = null;
        try
        {
            dynamic dynamicItem = item;
            imageFile = dynamicItem.Transfer(formatId)
                ?? throw new InvalidOperationException("WIA Transfer 返回 null。");

            tempPath = Path.Combine(Path.GetTempPath(), $"ipscan-wia-{Guid.NewGuid():N}.png");
            dynamic file = imageFile;
            file.SaveFile(tempPath);

            var bytes = File.ReadAllBytes(tempPath);
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("WIA 传输结果为空（0 字节）。");
            }

            return bytes;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Temp cleanup is best effort; the OS reaps the temp folder anyway.
                }
            }

            TryRelease(ref imageFile);
        }
    }
}
