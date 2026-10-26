using System.Windows;
using System.Windows.Controls;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App.Presentation;

/// <summary>
/// The inline strip's two row shapes: an atomic [gap | page] unit for every page, plus the
/// trailing gap that closes the strip. The trailing slot is a STANDALONE item on purpose —
/// folding it into the last page's unit (the pre-2026-10-22 layout) widened that unit by a
/// slot and bumped the last page to the next row whenever the row had room for the tile but
/// not tile+gap. A separate item cannot change any page unit's width.
/// </summary>
public sealed class PdfStripItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Unit { get; set; }

    public DataTemplate? Gap { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is PdfPreviewGapViewModel ? Gap : Unit;
}
