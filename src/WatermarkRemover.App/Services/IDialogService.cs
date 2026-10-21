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

/// <summary>
/// App-styled prompt implementation: one <see cref="AppConfirmDialog"/> window replaces raw
/// MessageBox so every confirmation / info / warning / error in the product shares the shell's
/// visual language. Owner windows keep prompts modal to their window.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>Shared instance for call sites without a natural owner (view models).</summary>
    public static readonly DialogService Default = new();

    private readonly Window? _owner;

    public DialogService(Window? owner = null) => _owner = owner;

    public bool Confirm(string message, string title) =>
        Show(message, title, MessageBoxButton.OKCancel, PromptKind.Warning);

    public bool ConfirmYesNo(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNo, PromptKind.Question);

    public void Info(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, PromptKind.Information);

    public void Warn(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, PromptKind.Warning);

    public void Error(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, PromptKind.Error);

    private bool Show(string message, string title, MessageBoxButton buttons, PromptKind kind) =>
        AppConfirmDialog.Show(_owner ?? Application.Current?.MainWindow, title, message, kind, buttons);
}
