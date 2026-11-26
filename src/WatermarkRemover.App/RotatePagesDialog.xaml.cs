using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>One page offered to the rotation picker: zero-based index + its strip thumbnail.</summary>
public sealed record RotatePageItem(int Index, ImageSource? Thumbnail);

/// <summary>One page's final clockwise quarter turns (0–3) chosen in the picker.</summary>
public sealed record PageRotation(int Index, int QuarterTurns);

/// <summary>One selectable page card in <see cref="RotatePagesDialog"/>.</summary>
public sealed class RotatePageOption : ObservableObject
{
    private bool _isSelected;
    private int _quarterTurns;

    public RotatePageOption(RotatePageItem item)
    {
        Index = item.Index;
        Thumbnail = item.Thumbnail;
        Caption = $"第 {item.Index + 1} 页";
    }

    /// <summary>Raised when the tick or the pending angle changes (dialog re-summarises).</summary>
    public event Action? Changed;

    public int Index { get; }

    public string Caption { get; }

    public ImageSource? Thumbnail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            // 取消勾选就等于把这页移出本次操作：清掉已预览的角度，避免“看不见的待旋转页”。
            if (!value) ResetTurns();
            Changed?.Invoke();
        }
    }

    /// <summary>Preview angle applied to the card's thumbnail (0/90/180/270).</summary>
    public int Angle => _quarterTurns * 90;

    public int QuarterTurns => _quarterTurns;

    public string StatusText => _quarterTurns == 0 ? "未旋转" : $"已转 {_quarterTurns * 90}°";

    /// <summary>One press of the rotate button: +90° for this page (wraps after 270°).</summary>
    internal void AddQuarterTurn()
    {
        _quarterTurns = (_quarterTurns + 1) % 4;
        OnPropertyChanged(nameof(Angle));
        OnPropertyChanged(nameof(StatusText));
        Changed?.Invoke();
    }

    private void ResetTurns()
    {
        if (_quarterTurns == 0) return;
        _quarterTurns = 0;
        OnPropertyChanged(nameof(Angle));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>
/// 按页旋转的选择器：勾选任意页（支持全选、反复点选、奇数页、偶数页），点「旋转」把勾选页
/// 顺时针加 90°，可连续点预览最终角度，最后「保存旋转」把全部改动一次性写进 PDF。
/// 取消返回 null，文件不动。
/// </summary>
public partial class RotatePagesDialog : Window
{
    private readonly List<RotatePageOption> _pages = new();

    private RotatePagesDialog(IReadOnlyList<RotatePageItem> pages)
    {
        InitializeComponent();
        foreach (RotatePageItem item in pages)
        {
            var option = new RotatePageOption(item);
            option.Changed += UpdateState;
            _pages.Add(option);
        }

        PagesList.ItemsSource = _pages;
        UpdateState();
    }

    /// <summary>Null when cancelled; otherwise only pages whose final angle is non-zero.</summary>
    public IReadOnlyList<PageRotation>? Choice { get; private set; }

    public static IReadOnlyList<PageRotation>? Show(Window? owner, IReadOnlyList<RotatePageItem> pages)
    {
        var window = new RotatePagesDialog(pages);
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

    private void UpdateState()
    {
        int selected = _pages.Count(p => p.IsSelected);
        int pending = _pages.Count(p => p.IsSelected && p.QuarterTurns != 0);

        RotateButton.IsEnabled = selected > 0;
        ApplyButton.IsEnabled = pending > 0;
        SummaryText.Text = pending > 0 ? $"{pending} 页待旋转，保存后一次性写入" : string.Empty;
        HintText.Text = selected == 0
            ? "勾选要旋转的页面：可以全选、多选，或直接选奇数页 / 偶数页。"
            : $"已勾选 {selected} 页。点「旋转」每次顺时针 90°，可连续点，最后一次性保存。";
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetSelection(_ => true);

    private void OnSelectOddClick(object sender, RoutedEventArgs e) =>
        SetSelection(page => page.Index % 2 == 0);   // 0 基下标：第 1、3、5… 页

    private void OnSelectEvenClick(object sender, RoutedEventArgs e) =>
        SetSelection(page => page.Index % 2 == 1);   // 第 2、4、6… 页

    private void OnClearClick(object sender, RoutedEventArgs e) => SetSelection(_ => false);

    private void SetSelection(Func<RotatePageOption, bool> predicate)
    {
        foreach (RotatePageOption page in _pages) page.IsSelected = predicate(page);
        UpdateState();
    }

    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        foreach (RotatePageOption page in _pages.Where(p => p.IsSelected))
            page.AddQuarterTurn();
        UpdateState();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        List<PageRotation> rotations = _pages
            .Where(p => p.IsSelected && p.QuarterTurns != 0)
            .Select(p => new PageRotation(p.Index, p.QuarterTurns))
            .ToList();
        if (rotations.Count == 0) return;

        Choice = rotations;
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
