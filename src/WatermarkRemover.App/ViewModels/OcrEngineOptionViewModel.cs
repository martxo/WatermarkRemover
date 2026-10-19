using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One radio row of the settings OCR section — used by both pipeline groups (layout block and
/// text engine): a registered option (name + description straight from the chain registration)
/// plus its probed availability. The group's preference accessors are injected, so each group
/// writes its own AppSettings property while sharing this single row type. Selecting the row
/// writes the option name into the working copy of <see cref="AppSettings"/>; the auto /
/// passthrough row writes an empty or default name, which the chain resolves to its head.
/// </summary>
public sealed class OcrEngineOptionViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings, string> _write;
    private IReadOnlyList<OcrEngineOptionViewModel> _group = Array.Empty<OcrEngineOptionViewModel>();
    private bool _isSelected;
    private bool _isProbing;
    private bool _isAvailable;
    private string _statusText = "检测中…";

    /// <param name="settings">Working copy of the settings; the selection is written there.</param>
    /// <param name="name">Factory name persisted as the preference; empty = the auto row.</param>
    /// <param name="title">Radio label (the auto row uses a friendlier wording).</param>
    /// <param name="description">One-line explanation shown under the label.</param>
    /// <param name="read">Reads the group's current preference from the settings object.</param>
    /// <param name="write">Writes the group's preference (one per pipeline stage).</param>
    /// <param name="groupName">RadioButton group name — keeps the two radio groups exclusive.</param>
    public OcrEngineOptionViewModel(
        AppSettings settings,
        string name,
        string title,
        string description,
        Func<AppSettings, string> read,
        Action<AppSettings, string> write,
        string groupName)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        Name = name;
        Title = title;
        Description = description;
        GroupName = groupName;

        _isProbing = name.Length > 0;
        _statusText = name.Length > 0 ? "检测中…" : string.Empty;
        _isSelected = string.Equals(
            (read ?? throw new ArgumentNullException(nameof(read)))(settings), name,
            StringComparison.Ordinal);
    }

    /// <summary>Value persisted by this group's preference accessor; empty = the auto row.</summary>
    public string Name { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>WPF RadioButton group name — one group per pipeline stage.</summary>
    public string GroupName { get; }

    /// <summary>Status shown right-aligned: 检测中… / 可用 / 不可用（原因）.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>True until the background availability probe reported this engine.</summary>
    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (SetProperty(ref _isProbing, value)) OnPropertyChanged(nameof(IsEnabled));
        }
    }

    /// <summary>Whether the engine answered the probe and can actually be chosen.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(IsEnabled));
        }
    }

    /// <summary>Radio enabled state: the auto row always, engine rows only once proven usable.</summary>
    public bool IsEnabled => !IsProbing && (Name.Length == 0 || IsAvailable);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value) || !value) return;

            _write(_settings, Name);

            // Belt and braces beside the RadioButton group name: the exclusive state is owned by
            // the view model so the persisted preference can never drift from what is shown.
            foreach (OcrEngineOptionViewModel other in _group)
            {
                if (!ReferenceEquals(other, this) && other._isSelected)
                    other.IsSelected = false;
            }
        }
    }

    /// <summary>Sibling rows of the same radio group, wired up after the list is built.</summary>
    internal void AttachGroup(IReadOnlyList<OcrEngineOptionViewModel> group) => _group = group;

    /// <summary>Records the probe outcome for this engine (UI-thread only).</summary>
    internal void UpdateAvailability(bool available, string reason)
    {
        IsProbing = false;
        IsAvailable = available;
        StatusText = available
            ? "可用"
            : reason.Length == 0 ? "不可用" : $"不可用（{reason}）";
    }
}
