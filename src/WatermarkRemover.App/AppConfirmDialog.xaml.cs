using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>Icon chip + affirmative-button colour for a prompt kind.</summary>
public enum PromptKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>
/// Thin window over the app-styled prompt layout. All state lives in the bound
/// <see cref="AppPromptViewModel"/>; this class only hosts it and maps the result back.
/// </summary>
public partial class AppConfirmDialog : Window
{
    public AppConfirmDialog(AppPromptViewModel prompt)
    {
        InitializeComponent();
        DataContext = prompt;
        prompt.CloseRequest += Close;
    }

    /// <summary>Shows the prompt modally; true when the affirmative button was chosen.</summary>
    public static bool Show(Window? owner, string title, string message, PromptKind kind, MessageBoxButton buttons)
    {
        var prompt = new AppPromptViewModel(title, message, kind, buttons);
        var window = new AppConfirmDialog(prompt);
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }
        window.ShowDialog();
        return prompt.Accepted;
    }

    private void OnTitleDragMove(object sender, MouseButtonEventArgs e)
    {
        // Buttons inside the title row handle their own presses, so this only drags.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Presentation model for one app-styled prompt: kind-driven icon chip, kind-driven
/// affirmative colour (danger for Warning/Error) and button pair labels. Accepted reports
/// whether the affirmative button closed the prompt.
/// </summary>
public sealed class AppPromptViewModel : ObservableObject
{
    /// <summary>Raised when either button was pressed; the window closes itself.</summary>
    public event Action? CloseRequest;

    private bool _accepted;

    public AppPromptViewModel(string title, string message, PromptKind kind, MessageBoxButton buttons)
    {
        Title = title;
        Message = message;

        bool danger = kind is PromptKind.Warning or PromptKind.Error;
        IconGlyph = kind switch
        {
            PromptKind.Information => "\uE946",
            PromptKind.Question => "\uE897",
            PromptKind.Error => "\uEB90",
            _ => "\uE7BA",
        };
        IconBackground = ResourceBrush(danger ? "DangerSurfaceBrush" : "AccentSoftBrush");
        IconForeground = ResourceBrush(danger ? "DangerBrush" : "AccentBrush");
        IsDanger = danger;
        IsSafe = !danger;

        (AcceptLabel, CancelLabel) = buttons switch
        {
            MessageBoxButton.YesNo => ("是", "否"),
            MessageBoxButton.OK => ("确定", null),
            _ => ("确定", "取消"),
        };

        AcceptCommand = new RelayCommand(() => { _accepted = true; CloseRequest?.Invoke(); });
        DismissCommand = new RelayCommand(() => CloseRequest?.Invoke());
    }

    public string Title { get; }
    public string Message { get; }
    public string IconGlyph { get; }
    public Brush IconBackground { get; }
    public Brush IconForeground { get; }
    public string AcceptLabel { get; }
    public string? CancelLabel { get; }
    public bool HasCancel => CancelLabel is not null;
    public bool IsDanger { get; }
    public bool IsSafe { get; }
    public bool Accepted => _accepted;

    public ICommand AcceptCommand { get; }
    public ICommand DismissCommand { get; }

    /// <summary>Palette lookup with a transparent fallback so design-time/test hosts never throw.</summary>
    private static Brush ResourceBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
