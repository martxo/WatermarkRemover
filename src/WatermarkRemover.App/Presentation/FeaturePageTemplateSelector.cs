using System.Windows;
using System.Windows.Controls;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.App.Presentation;

/// <summary>
/// The shell's ViewLocator: maps a feature's declared <see cref="FeaturePageKind"/> to the page
/// DataTemplate that renders it. Adding a feature that reuses an existing page kind needs no
/// shell change at all; a genuinely new page kind is one template plus one switch arm.
/// </summary>
public sealed class FeaturePageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FilesTemplate { get; set; }

    public DataTemplate? ScanTemplate { get; set; }

    public DataTemplate? SearchTemplate { get; set; }

    public DataTemplate? NetdiskTemplate { get; set; }

    public DataTemplate? TransferTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is IFeature feature
            ? FeaturePresentationCatalog.For(feature).PageKind switch
            {
                FeaturePageKind.Scan => ScanTemplate,
                FeaturePageKind.Search => SearchTemplate,
                FeaturePageKind.Netdisk => NetdiskTemplate,
                FeaturePageKind.Transfer => TransferTemplate,
                _ => FilesTemplate,
            }
            : FilesTemplate;
}
