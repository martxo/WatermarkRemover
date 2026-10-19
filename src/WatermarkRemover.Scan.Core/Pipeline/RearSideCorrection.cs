using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Core.Pipeline;

/// <summary>How a rear side has to be reoriented before it joins the document.</summary>
public enum RearSideTransform
{
    None,
    Rotate180,
}

/// <summary>
/// Resolves the reorientation of an ADF duplex rear image. Mirrors the target's
/// 「将背面图像自动旋转 180 度」 (<c>SCN_ROTATE_IMAGE_AUTO_ROTATE REAR_IMAGE_180</c>) as a pure function so
/// the rule is unit-testable and every caller agrees on one answer.
/// </summary>
/// <remarks>
/// The target exposes this as one opt-in toggle with no binding-specific variant, and that is how it is
/// modelled here: a single 180° correction for rear pages, default off (the target's 自动图像旋转
/// engine parameter ships off, so duplex output relies on the driver delivering upright rear pages).
/// The binding (<see cref="DuplexMode.LongEdge"/> vs <see cref="DuplexMode.ShortEdge"/>) is kept explicit
/// rather than collapsed into "duplex is on" because it still decides how the finished document is bound
/// and printed - the print side maps LongEdge to a vertical duplex and ShortEdge to a horizontal one.
/// Devices that already deliver rear pages upright simply leave <c>RearSideAutoRotate</c> off.
/// </remarks>
public static class RearSideCorrection
{
    /// <summary>The binding edge the sheet turns about; null when duplex is off.</summary>
    public static DuplexMode? ResolveBinding(ScanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Duplex == DuplexMode.Off ? null : settings.Duplex;
    }

    /// <summary>Transform to apply to a rear side, or <see cref="RearSideTransform.None"/>.</summary>
    public static RearSideTransform Resolve(ScanSettings settings, bool isBackSide)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!isBackSide || settings.Duplex == DuplexMode.Off || !settings.RearSideAutoRotate)
        {
            return RearSideTransform.None;
        }

        return RearSideTransform.Rotate180;
    }

    /// <summary>Human readable explanation of what the binding implies, for the diagnostic log.</summary>
    public static string? Describe(ScanSettings settings)
    {
        var binding = ResolveBinding(settings);
        if (binding is null)
        {
            return null;
        }

        return binding switch
        {
            DuplexMode.LongEdge => "双面长边翻页：背面按 180° 纠正（装订边为长边，双面打印映射为纵向翻页）。",
            DuplexMode.ShortEdge => "双面短边翻页：背面按 180° 纠正（装订边为短边，双面打印映射为横向翻页）。",
            _ => null,
        };
    }
}
