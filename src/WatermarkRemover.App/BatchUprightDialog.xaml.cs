using System.Windows;
using System.Windows.Input;

namespace WatermarkRemover.App;

/// <summary>User's choice for one-click batch upright: clockwise quarter turns + scope.</summary>
public sealed record BatchUprightChoice(int QuarterTurns, bool OnlyUndecided);

/// <summary>
/// Small app-styled chooser for the strip's 「批量摆正」 entry: whole document vs only the pages
/// the per-page verdict left undecided, and 90° / 180° / 270°. All state is read back from the
/// radio buttons on accept; cancel returns null.
/// </summary>
public partial class BatchUprightDialog : Window
{
    private readonly int _undecidedCount;

    private BatchUprightDialog(int undecidedCount)
    {
        InitializeComponent();
        _undecidedCount = undecidedCount;

        MessageText.Text = undecidedCount > 0
            ? $"{undecidedCount} 页未能自动摆正。选择方向和范围，一次改好并保存。"
            : "按选择的方向旋转文档，一次改好并保存。";

        ScopeUndecided.Content = undecidedCount > 0
            ? $"仅未摆正的 {undecidedCount} 页"
            : "仅未摆正的页（当前没有）";
        ScopeUndecided.IsEnabled = undecidedCount > 0;
    }

    /// <summary>Null when cancelled / closed; otherwise the chosen direction and scope.</summary>
    public BatchUprightChoice? Choice { get; private set; }

    public static BatchUprightChoice? Show(Window? owner, int undecidedCount)
    {
        var window = new BatchUprightDialog(Math.Max(0, undecidedCount));
        Window? resolved = AppConfirmDialog.ResolveOwner(owner);
        if (resolved is { IsLoaded: true })
        {
            window.Owner = resolved;
            // 与 AppConfirmDialog 同一条铁律：属主是置顶窗（全屏看图窗）时必须跟着置顶，
            // 否则模态框被压在全屏暗幕后面，看起来就是应用卡死。
            if (resolved.Topmost) window.Topmost = true;
        }
        window.ShowDialog();
        return window.Choice;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        int turns = Direction270.IsChecked == true ? 3
            : Direction180.IsChecked == true ? 2
            : 1;
        bool onlyUndecided = _undecidedCount > 0 && ScopeUndecided.IsChecked == true;
        Choice = new BatchUprightChoice(turns, onlyUndecided);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnTitleDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
