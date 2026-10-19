namespace WatermarkRemover.Scan.Core.Models;

/// <summary>A scan device reported by an <c>IScanSource</c>.</summary>
public sealed class ScanDeviceInfo
{
    public required string SourceId { get; init; }

    /// <summary>Source specific identity, e.g. a WIA device id string.</summary>
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    public string Manufacturer { get; init; } = string.Empty;

    /// <summary>Capability flags reported by the driver; free-form so new sources can add their own.</summary>
    public IReadOnlyDictionary<string, string> Capabilities { get; init; } =
        new Dictionary<string, string>();

    public ScanDeviceInfo Clone() => new()
    {
        SourceId = SourceId,
        DeviceId = DeviceId,
        Name = Name,
        Manufacturer = Manufacturer,
        Capabilities = new Dictionary<string, string>(Capabilities),
    };
}

/// <summary>Destination kinds that an output target can write to.</summary>
public enum OutputKind
{
    Folder,
    Pdf,
    Email,
    Ftp,
    Printer,
}

/// <summary>Configuration for a single output target.</summary>
public sealed class OutputSettings
{
    public required OutputKind Kind { get; init; }

    // --- Folder / PDF -----------------------------------------------------
    public string Directory { get; set; } = string.Empty;

    /// <summary>File name template; supports the $yyyy$MM$dd macro syntax and {index}/{type}/{name}.</summary>
    public string FileNameTemplate { get; set; } = "Scan_$yyyy$MM$dd_HHmmss";

    public string ImageFormat { get; set; } = "pdf";

    /// <summary>Emit an invisible OCR text layer so the PDF becomes searchable (differentiator).</summary>
    public bool SearchablePdf { get; set; } = true;

    /// <summary>Add a page-number stamp to the bottom of every page.</summary>
    public bool AddPageNumbers { get; set; }

    /// <summary>Write a sidecar .txt with the OCR text next to the document.</summary>
    public bool SaveTextSidecar { get; set; }

    /// <summary>Password for the encrypted PDF output (SCN_PASSWORD_PDF); empty = plain PDF.</summary>
    public string PdfPassword { get; set; } = string.Empty;

    /// <summary>
    /// 高压缩 PDF (SCN_HIGH_COMPRESSION_PDF): embedded pages are downsampled to 300dpi and
    /// JPEG-encoded instead of stored lossless — much smaller files, slightly softer images.
    /// </summary>
    public bool HighCompressionPdf { get; set; }

    /// <summary>Compression codec for multi-page TIFF output (SCN_COMPRESSION_TYPE_*).</summary>
    public TiffCompressionMode TiffCompression { get; set; } = TiffCompressionMode.LZW;

    // --- Email ------------------------------------------------------------
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string MailFrom { get; set; } = string.Empty;
    public string MailTo { get; set; } = string.Empty;
    public string MailSubject { get; set; } = string.Empty;
    public string MailBody { get; set; } = string.Empty;

    // --- FTP --------------------------------------------------------------
    public string FtpHost { get; set; } = string.Empty;
    public int FtpPort { get; set; } = 21;
    public bool FtpUseSftp { get; set; }
    public string FtpUser { get; set; } = string.Empty;
    public string FtpPassword { get; set; } = string.Empty;
    public string FtpDirectory { get; set; } = "/";

    // --- Printer ----------------------------------------------------------
    public string PrinterName { get; set; } = string.Empty;
    public int Copies { get; set; } = 1;
    public PrintQuality Quality { get; set; } = PrintQuality.Normal;
    public PageLayout Layout { get; set; } = PageLayout.OneInOne;
    public DuplexMode PrintDuplex { get; set; } = DuplexMode.Off;
    public bool Borderless { get; set; }

    public OutputSettings Clone() => (OutputSettings)MemberwiseClone();
}

/// <summary>What an output target produced, for the UI and for the run log.</summary>
public sealed class OutputResult
{
    public required bool Success { get; init; }

    public required string TargetId { get; init; }

    /// <summary>Local files written by this target, if any.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    public string? Message { get; init; }

    public string? ErrorDetail { get; init; }

    /// <summary>True when the target consumed the document and following targets should be skipped.</summary>
    public bool IsTerminal { get; init; }

    public static OutputResult Ok(string targetId, string? message = null, params string[] files) => new()
    {
        Success = true,
        TargetId = targetId,
        Message = message,
        Files = files,
    };

    public static OutputResult Fail(string targetId, string message, string? detail = null) => new()
    {
        Success = false,
        TargetId = targetId,
        Message = message,
        ErrorDetail = detail,
    };
}
