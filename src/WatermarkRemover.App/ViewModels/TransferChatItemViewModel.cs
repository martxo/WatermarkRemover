using System.IO;
using WatermarkRemover.Core.Features.Transfer;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One bubble in the 手机传文件 chat on the computer side.</summary>
public sealed class TransferChatItemViewModel : ObservableObject
{
    private string _linkStateText = string.Empty;
    private double _linkProgress = -1;
    private bool _linkBusy;
    private bool _linkReady;
    private bool _linkFailed;

    public TransferChatItemViewModel(
        TransferChatMessage message,
        string? localPath = null,
        Action<TransferChatItemViewModel>? open = null,
        Action<TransferChatItemViewModel>? reveal = null,
        Action<TransferChatItemViewModel>? openPdf = null)
    {
        IsMine = message.Direction == TransferChatDirection.Computer;
        IsText = message.Kind == TransferChatKind.Text;
        IsImage = message.Kind == TransferChatKind.Image;
        IsLink = message.Kind == TransferChatKind.Link;
        Text = message.Text;
        FileName = message.FileName;
        SizeText = IsText ? string.Empty : FormatSize(message.Size);
        TimeText = message.CreatedAt.ToString("HH:mm");

        LocalPath = localPath;
        HasPath = localPath is not null && File.Exists(localPath);
        Directory = HasPath ? Path.GetDirectoryName(localPath!) ?? string.Empty : string.Empty;
        ExtensionText = IsFile ? ExtensionOf(FileName) : string.Empty;

        OpenCommand = new RelayCommand(() => open?.Invoke(this), () => HasPath && IsFile);
        OpenFolderCommand = new RelayCommand(() => reveal?.Invoke(this), () => HasPath);
        OpenPdfCommand = new RelayCommand(() => openPdf?.Invoke(this), () => IsLink && LinkReady);

        if (IsLink)
        {
            // Resolving the link is the first, open-ended stage: the bar marches until the
            // provider reports a page count.
            LinkBusy = true;
            LinkProgress = -1;
            LinkStateText = "正在解析链接…";
        }
    }

    /// <summary>Computer-sent rows align right; phone rows align left.</summary>
    public bool IsMine { get; }

    public bool IsText { get; }

    public bool IsImage { get; }

    /// <summary>A 扫描全能王-style share link: the row tracks its own download progress.</summary>
    public bool IsLink { get; }

    public bool IsFile => !IsText && !IsLink;

    public string Text { get; }

    public string FileName { get; }

    public string SizeText { get; }

    public string TimeText { get; }

    public string KindText => IsImage ? "图片" : "文件";

    /// <summary>Extension badge shown on file rows ("PDF"/"PNG"), WeChat file-card style.</summary>
    public string ExtensionText { get; }

    /// <summary>File behind this bubble (phone upload target or computer outbox copy).</summary>
    public string? LocalPath { get; }

    public bool HasPath { get; }

    /// <summary>Folder line under the file name; clicking it reveals the containing folder.</summary>
    public string Directory { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Jumps to the PDF processing page with the link's document loaded.</summary>
    public RelayCommand OpenPdfCommand { get; }

    /// <summary>Document title reported by the share provider, once known.</summary>
    public string LinkTitle { get; private set; } = string.Empty;

    public bool HasLinkTitle => LinkTitle.Length > 0;

    /// <summary>Current stage of the link chain (解析 → 下载 x/N → 就绪 / 失败).</summary>
    public string LinkStateText
    {
        get => _linkStateText;
        private set
        {
            if (SetProperty(ref _linkStateText, value))
                OnPropertyChanged(nameof(ShowLinkSpinner));
        }
    }

    /// <summary>Determinate fraction while pages download; -1 while the stage length is unknown.</summary>
    public double LinkProgress
    {
        get => _linkProgress;
        private set
        {
            if (SetProperty(ref _linkProgress, value))
                OnPropertyChanged(nameof(LinkProgressIndeterminate));
        }
    }

    public bool LinkBusy
    {
        get => _linkBusy;
        private set
        {
            if (SetProperty(ref _linkBusy, value))
            {
                OnPropertyChanged(nameof(ShowLinkSpinner));
                OnPropertyChanged(nameof(LinkProgressIndeterminate));
            }
        }
    }

    /// <summary>Pages (or a finished PDF) are ready — the 去处理 PDF button lights up.</summary>
    public bool LinkReady
    {
        get => _linkReady;
        private set
        {
            if (SetProperty(ref _linkReady, value)) OpenPdfCommand.RaiseCanExecuteChanged();
        }
    }

    public bool LinkFailed
    {
        get => _linkFailed;
        private set
        {
            if (SetProperty(ref _linkFailed, value))
                OnPropertyChanged(nameof(ShowLinkSpinner));
        }
    }

    /// <summary>A marching indicator is shown while the chain runs and no failure was reported.</summary>
    public bool ShowLinkSpinner => LinkBusy && !LinkFailed;

    /// <summary>True while the stage length is unknown (link resolution), so the bar marches.</summary>
    public bool LinkProgressIndeterminate => LinkProgress < 0 && LinkBusy;

    public void ReportLinkTitle(string title)
    {
        LinkTitle = title;
        OnPropertyChanged(nameof(HasLinkTitle));
    }

    public void ReportLinkStage(string text, double progress)
    {
        LinkStateText = text;
        LinkProgress = progress;
    }

    public void ReportLinkReady(string text)
    {
        LinkBusy = false;
        LinkReady = true;
        LinkStateText = text;
        LinkProgress = 1;
    }

    public void ReportLinkFailure(string text)
    {
        LinkBusy = false;
        LinkFailed = true;
        LinkStateText = text;
    }

    private static string ExtensionOf(string name)
    {
        string extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return extension.Length > 4 ? extension[..4] : extension;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB",
    };
}
