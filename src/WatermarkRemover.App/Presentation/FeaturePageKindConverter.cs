using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WatermarkRemover.App.Presentation;

/// <summary>
/// Shell-layout values derived from the selected page kind, so the window needs no per-feature
/// triggers: the search page spans both content rows, every other page keeps the job list row.
/// </summary>
public sealed class FeaturePageKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        FeaturePageKind kind = value is FeaturePageKind pageKind ? pageKind : FeaturePageKind.Files;
        return (parameter as string) switch
        {
            "RowSpan" => kind is FeaturePageKind.Search or FeaturePageKind.Transfer ? 2 : 1,
            // 快速搜索 and 手机传文件 carry their own lists; the shared job board underneath is
            // redundant there (the chat is the record).
            "JobList" => kind is FeaturePageKind.Search or FeaturePageKind.Transfer
                ? Visibility.Collapsed
                : Visibility.Visible,
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
