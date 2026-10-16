using System.Windows;

namespace WatermarkRemover.App.Services;

/// <summary>
/// User-facing dialog prompts. View models talk to this interface instead of MessageBox
/// directly, so prompts can be faked in tests and restyled (custom dialogs, translation) in
/// one place.
/// </summary>
public interface IDialogService
{
    /// <summary>OK/Cancel confirmation; true when the user accepts.</summary>
    bool Confirm(string message, string title);

    /// <summary>Yes/No confirmation for "shall I do X now?" prompts; true when the user accepts.</summary>
    bool ConfirmYesNo(string message, string title);

    void Info(string message, string title);

    void Warn(string message, string title);

    void Error(string message, string title);
}

/// <summary>MessageBox-backed implementation. Owner windows keep prompts modal to their window.</summary>
public sealed class DialogService : IDialogService
{
    /// <summary>Shared instance for call sites without a natural owner (view models).</summary>
    public static readonly DialogService Default = new();

    private readonly Window? _owner;

    public DialogService(Window? owner = null) => _owner = owner;

    public bool Confirm(string message, string title) =>
        Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    public bool ConfirmYesNo(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private MessageBoxResult Show(string message, string title, MessageBoxButton button, MessageBoxImage image) =>
        _owner is null
            ? MessageBox.Show(message, title, button, image)
            : MessageBox.Show(_owner, message, title, button, image);
}
