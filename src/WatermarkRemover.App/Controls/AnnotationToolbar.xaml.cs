using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using WatermarkRemover.App.Editing;

namespace WatermarkRemover.App.Controls;

/// <summary>工具条皮肤：截图遮罩用深色，标注窗等浅色宿主用浅色。</summary>
public enum AnnotationToolbarSkin
{
    Dark,
    Light,
}

/// <summary>
/// 统一标注工具条（截图遮罩 / 标注窗 / 后续宿主共用）。工具按钮由
/// <see cref="AnnotationToolRegistry"/> 生成——注册新工具即自动出现在工具条上；
/// 宿主通过事件接入自己的会话，并通过 <see cref="Sync"/> 回写选中状态。
/// </summary>
public partial class AnnotationToolbar : UserControl
{
    private static readonly (uint Argb, string Name)[] Colors =
    {
        (0xFFE53935, "红色标注"),
        (0xFFFBC02D, "黄色标注"),
        (0xFF1E88E5, "蓝色标注"),
        (0xFF43A047, "绿色标注"),
        (0xFF111111, "黑色标注"),
    };

    private static readonly (float Dip, string Short, string Name)[] Widths =
    {
        (1.5f, "细", "细线条"),
        (3f, "中", "中等线条"),
        (5f, "粗", "粗线条"),
    };

    private static readonly float[] FontSizes = { 12f, 16f, 20f };

    private readonly List<ToggleButton> _toolButtons = new();
    private readonly List<ToggleButton> _widthButtons = new();
    private readonly List<Button> _colorButtons = new();
    private readonly List<Rectangle> _separators = new();
    private Button? _undoButton;
    private Button? _redoButton;
    private Button? _clearButton;
    private ComboBox? _fontSizeBox;
    private bool _syncing;

    public AnnotationToolbar()
    {
        InitializeComponent();
        Build();
        ApplySkin();
    }

    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(AnnotationToolbarSkin), typeof(AnnotationToolbar),
        new PropertyMetadata(AnnotationToolbarSkin.Dark, OnSkinPropertyChanged));

    public AnnotationToolbarSkin Skin
    {
        get => (AnnotationToolbarSkin)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    /// <summary>点击了某个工具（含再次点击取消——由会话的切换语义处理）。</summary>
    public event EventHandler<ScreenshotTool>? ToolClick;

    public event EventHandler<uint>? ColorClick;

    public event EventHandler<float>? WidthClick;

    public event EventHandler<float>? FontSizeClick;

    public event EventHandler? UndoClick;

    public event EventHandler? RedoClick;

    public event EventHandler? ClearClick;

    private static void OnSkinPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AnnotationToolbar)d).ApplySkin();
    }

    // --- 构建（注册表驱动） ------------------------------------------------------

    private void Build()
    {
        foreach (IAnnotationTool tool in AnnotationToolRegistry.Default.Tools)
        {
            var button = new ToggleButton
            {
                Content = tool.DisplayName,
                ToolTip = $"{tool.Description}（{tool.Shortcut}）",
                Tag = tool.Tool,
            };
            button.Click += (_, _) => ToolClick?.Invoke(this, tool.Tool);
            _toolButtons.Add(button);
            Root.Children.Add(button);
        }

        Root.Children.Add(Separator());

        foreach ((uint argb, string name) in Colors)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(MarkBrush(argb)),
                ToolTip = name,
                Tag = argb,
            };
            button.Click += (_, _) => ColorClick?.Invoke(this, argb);
            _colorButtons.Add(button);
            Root.Children.Add(button);
        }

        Root.Children.Add(Separator());

        foreach ((float dip, string shortName, string name) in Widths)
        {
            var button = new ToggleButton
            {
                Content = shortName,
                ToolTip = name,
                Tag = dip,
            };
            button.Click += (_, _) => WidthClick?.Invoke(this, dip);
            _widthButtons.Add(button);
            Root.Children.Add(button);
        }

        Root.Children.Add(Separator());

        _fontSizeBox = new ComboBox
        {
            Width = 64,
            Height = 24,
            FontSize = 11,
            SelectedIndex = 1,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "文字字号",
        };
        _fontSizeBox.Items.Add("小 12");
        _fontSizeBox.Items.Add("中 16");
        _fontSizeBox.Items.Add("大 20");
        _fontSizeBox.SelectionChanged += OnFontSizeSelectionChanged;
        Root.Children.Add(_fontSizeBox);

        Root.Children.Add(Separator());

        _undoButton = Action("撤销", "撤销上一步标注（Ctrl+Z）", () => UndoClick?.Invoke(this, EventArgs.Empty));
        _redoButton = Action("重做", "恢复刚才撤销的标注（Ctrl+Y）", () => RedoClick?.Invoke(this, EventArgs.Empty));
        _clearButton = Action("清空", "清除全部标注", () => ClearClick?.Invoke(this, EventArgs.Empty));

        Root.Children.Add(_undoButton);
        Root.Children.Add(_redoButton);
        Root.Children.Add(_clearButton);
    }

    private Rectangle Separator()
    {
        var line = new Rectangle { Width = 1, Margin = new Thickness(6, 2, 6, 2) };
        _separators.Add(line);
        return line;
    }

    private Button Action(string content, string toolTip, Action onClick)
    {
        var button = new Button { Content = content, ToolTip = toolTip };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnFontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _fontSizeBox is not { } box) return;
        if (box.SelectedIndex < 0 || box.SelectedIndex >= FontSizes.Length) return;
        FontSizeClick?.Invoke(this, FontSizes[box.SelectedIndex]);
    }

    // --- 皮肤 ----------------------------------------------------------------------

    private void ApplySkin()
    {
        bool dark = Skin == AnnotationToolbarSkin.Dark;
        Style toggle = (Style)Resources[dark ? "ToggleDark" : "ToggleLight"];
        Style button = (Style)Resources[dark ? "ButtonDark" : "ButtonLight"];
        Style swatch = (Style)Resources[dark ? "SwatchDark" : "SwatchLight"];
        Color separator = dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0xE0, 0xE0, 0xE0);

        foreach (ToggleButton item in _toolButtons) item.Style = toggle;
        foreach (ToggleButton item in _widthButtons) item.Style = toggle;
        foreach (Button item in _colorButtons) item.Style = swatch;
        if (_undoButton is not null) _undoButton.Style = button;
        if (_redoButton is not null) _redoButton.Style = button;
        if (_clearButton is not null) _clearButton.Style = button;

        foreach (Rectangle line in _separators)
            line.Fill = new SolidColorBrush(separator);

        // 撤销/重做之间的间距与浅色宿主原有布局一致；深色皮肤原本无间距。
        Thickness actionMargin = dark ? default : new Thickness(0, 0, 4, 0);
        if (_undoButton is not null) _undoButton.Margin = actionMargin;
        if (_redoButton is not null) _redoButton.Margin = actionMargin;
        if (_clearButton is not null) _clearButton.Margin = default;

        if (_fontSizeBox is not null)
        {
            _fontSizeBox.Margin = dark ? new Thickness(2, 0, 2, 0) : new Thickness(3, 0, 0, 0);
            _fontSizeBox.FontSize = dark ? 11 : 12;
        }
    }

    // --- 状态回写 -------------------------------------------------------------------

    /// <summary>把会话的当前选择同步到按钮上（每次宿主重绘时调用，幂等）。</summary>
    public void Sync(
        ScreenshotTool tool,
        uint colorArgb,
        float lineWidthDip,
        float textFontSizeDip,
        bool canUndo,
        bool canRedo,
        bool canClear)
    {
        foreach (ToggleButton button in _toolButtons)
            button.IsChecked = button.Tag is ScreenshotTool tag && tag == tool;

        foreach (Button button in _colorButtons)
            button.Opacity = button.Tag is uint argb && argb == colorArgb ? 1.0 : 0.45;

        foreach (ToggleButton button in _widthButtons)
            button.IsChecked = button.Tag is float dip && Math.Abs(dip - lineWidthDip) < 0.01f;

        if (_undoButton is not null) _undoButton.IsEnabled = canUndo;
        if (_redoButton is not null) _redoButton.IsEnabled = canRedo;
        if (_clearButton is not null) _clearButton.IsEnabled = canClear;

        if (_fontSizeBox is { } box)
        {
            int index = textFontSizeDip <= 12f ? 0 : textFontSizeDip >= 20f ? 2 : 1;
            if (box.SelectedIndex != index)
            {
                _syncing = true;
                try
                {
                    box.SelectedIndex = index;
                }
                finally
                {
                    _syncing = false;
                }
            }
        }
    }

    private static Color MarkBrush(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
