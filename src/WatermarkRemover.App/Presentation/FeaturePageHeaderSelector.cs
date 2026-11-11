using System.Windows;
using System.Windows.Controls;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.App.Presentation;

/// <summary>
/// The shell header's ViewLocator: picks the page-specific action strip shown on the title row
/// (right side). Same rule as the page selector — a feature's declared
/// <see cref="FeaturePageKind"/> decides, so no feature id appears in the shell. Pages with no
/// actions resolve to the EMPTY template: a null here makes the hosting ContentControl fall back
/// to rendering the feature object itself, whose ToString() is the full CLR type name — the
/// 「WatermarkRemover.Core.Features.Scan.ScanFeature」 line users saw on the scan page header.
/// </summary>
public sealed class FeaturePageHeaderSelector : DataTemplateSelector
{
    public DataTemplate? TransferHeaderTemplate { get; set; }

    public DataTemplate? FilesHeaderTemplate { get; set; }

    /// <summary>Renders nothing; used by every page kind without a title-row action strip.</summary>
    public DataTemplate? EmptyHeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is IFeature feature
            ? FeaturePresentationCatalog.For(feature).PageKind switch
            {
                FeaturePageKind.Transfer => TransferHeaderTemplate,
                FeaturePageKind.Files => FilesHeaderTemplate,
                _ => EmptyHeaderTemplate,
            }
            : EmptyHeaderTemplate;
}
