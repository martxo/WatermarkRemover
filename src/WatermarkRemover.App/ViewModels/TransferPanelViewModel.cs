using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Imaging;
using WatermarkRemover.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// The 手机传文件 page's view model: wraps one <see cref="LanTransferServer"/> with start/stop,
/// the QR code the phone scans, and a running tally of what came in. Received files are raised
/// through <see cref="FileReceived"/> on a socket thread — the owner (MainViewModel) marshals
/// them into the job list. This class holds no feature logic of its own.
/// </summary>
public sealed class TransferPanelViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IShellOpenService _shell;
    private readonly Action<string> _trace;
    private readonly IOcrEngine? _idCardOcr;
    private LanTransferServer? _server;

    private bool _isRunning;
    private bool _isConnected;
    private string _draftText = string.Empty;
    private string _urlText = string.Empty;
    private ImageSource? _qrImage;
    private string _statusText = "点击「开始接收」，生成二维码后用手机扫码。";
    private string _saveDirectoryText = string.Empty;
    private int _receivedCount;
    private long _receivedBytes;
    private string? _targetDirectory;
    private string _pendingTitle = string.Empty;
    private ComposeLayout _selectedLayout = ComposeLayoutCatalog.Single;
    private CropModeChoice _selectedCropMode = CropModeCatalog.All[0];
    private ImageSource? _composePreview;
    private bool _isComposing;
    private int _localRowId;
    private string? _lastLinkPdf;

    public TransferPanelViewModel(
        AppSettings settings,
        IShellOpenService shell,
        Action<string>? trace = null,
        IOcrEngine? idCardOcr = null)
    {
        _settings = settings;
        _shell = shell;
        _trace = trace ?? (_ => { });
        _idCardOcr = idCardOcr;

        ToggleCommand = new RelayCommand(() =>
        {
            if (IsRunning) Stop();
            else Start();
        });
        OpenFolderCommand = new RelayCommand(
            () => _shell.TryOpenDirectory(_saveDirectoryText, out _),
            () => IsRunning);
        SendTextCommand = new RelayCommand(
            SendText, () => IsRunning && DraftText.Trim().Length > 0);
        SendFileCommand = new RelayCommand(
            () => SendFileRequested?.Invoke(this, EventArgs.Empty), () => IsRunning);
        Chat.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChat));

        ComposeCommand = new RelayCommand(
            () => _ = ComposePendingAsync(SelectedLayout, SelectedCropMode),
            () => SelectedPageCount > 0 && !IsComposing);
        ComposeOriginalCommand = new RelayCommand(
            () => _ = ComposePendingAsync(ComposeLayoutCatalog.Single, CropModeCatalog.All[2]),
            () => HasPendingPages && !IsComposing);
        ClearPendingCommand = new RelayCommand(() =>
        {
            ClearPending();
            StatusText = "已取消本次链接图片。";
        }, () => HasPendingPages && !IsComposing);
        MovePendingPageCommand = new RelayCommand<ComposePageMove>(
            move => MovePendingPage(move.From, move.To));
    }

    /// <summary>Relayed from the server; fires on a socket thread per landed file.</summary>
    public event Action<string, long>? FileReceived;

    /// <summary>Raised when the computer's 「发送文件」 button is pressed; the host owns the picker
    /// and calls <see cref="SendFileAsync"/> with the chosen path.</summary>
    public event EventHandler? SendFileRequested;

    /// <summary>Images open in the app's shared viewer (the screenshot/PDF surface), not the
    /// shell; the host wires this to ImageViewerWindow.</summary>
    public event EventHandler<string>? OpenImageRequested;

    /// <summary>
    /// The user asked to take a share-link document to the PDF page: the parameter is the
    /// finished PDF when one already exists (or was just composed), null when the page should
    /// simply open. The owner switches pages and loads the file.
    /// </summary>
    public event Action<string?>? PdfPageRequested;

    public RelayCommand SendTextCommand { get; }

    public RelayCommand SendFileCommand { get; }

    /// <summary>A file composed from a share link is ready at this path. The flag tells the owner
    /// whether to run content naming: the 身份证 template already produces the final printable
    /// sheet and should not spend seconds re-OCRing it. Raised on a worker thread.</summary>
    public event Action<string, bool>? ShareDocumentReceived;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ComposeCommand { get; }

    /// <summary>「原样合并」: one page per image, no crop — the old mechanical behaviour, still
    /// one click away when the user does not want any packing.</summary>
    public RelayCommand ComposeOriginalCommand { get; }

    public RelayCommand ClearPendingCommand { get; }

    /// <summary>Drag-reorder callback from the gallery behaviour.</summary>
    public RelayCommand<ComposePageMove> MovePendingPageCommand { get; }

    /// <summary>Pages pulled from the last recognised CamScanner share, newest link replaces the
    /// previous selection. The gallery is where the user decides what becomes one file.</summary>
    public ObservableCollection<ComposeSourceViewModel> PendingPages { get; } = new();

    public IReadOnlyList<ComposeLayout> ComposeLayouts => ComposeLayoutCatalog.All;

    public IReadOnlyList<CropModeChoice> CropModes => CropModeCatalog.All;

    public ComposeLayout SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (value is null || !SetProperty(ref _selectedLayout, value)) return;
            RefreshPreview();
        }
    }

    public CropModeChoice SelectedCropMode
    {
        get => _selectedCropMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedCropMode, value)) return;
            RefreshPreview();
        }
    }

    /// <summary>First composed page as a small JPEG preview; regenerated whenever the selection,
    /// pack or crop mode changes, so the user sees the cut before committing.</summary>
    public ImageSource? ComposePreview
    {
        get => _composePreview;
        private set => SetProperty(ref _composePreview, value);
    }

    public bool HasPendingPages => PendingPages.Count > 0;

    public int SelectedPageCount => PendingPages.Count(p => p.IsSelected);

    public bool IsComposing
    {
        get => _isComposing;
        private set
        {
            if (!SetProperty(ref _isComposing, value)) return;
            RaiseComposeState();
        }
    }

    /// <summary>Chat-style log of texts posted from the phone, newest first; each row keeps its
    /// sender, server-side time and kind, and copies its full text back on demand.</summary>
    public ObservableCollection<ReceivedMessageViewModel> Messages { get; } = new();

    public bool HasMessages => Messages.Count > 0;

    /// <summary>The two-way conversation shown on the computer side, oldest first.</summary>
    public ObservableCollection<TransferChatItemViewModel> Chat { get; } = new();

    public bool HasChat => Chat.Count > 0;

    /// <summary>True once the phone opened the page; the QR hero then folds into the ⋯ menu.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                OnPropertyChanged(nameof(ShowQrHero));
        }
    }

    /// <summary>Show the big centered QR until the phone has opened the page.</summary>
    public bool ShowQrHero => !IsConnected;

    /// <summary>Text being composed on the computer; cleared after a successful send.</summary>
    public string DraftText
    {
        get => _draftText;
        set
        {
            if (SetProperty(ref _draftText, value)) SendTextCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ToggleText));
                SendTextCommand.RaiseCanExecuteChanged();
                SendFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ToggleText => IsRunning ? "停止接收" : "开始接收";

    /// <summary>The address encoded into the QR code; empty while stopped.</summary>
    public string UrlText
    {
        get => _urlText;
        private set => SetProperty(ref _urlText, value);
    }

    /// <summary>The QR bitmap (frozen, thread-safe); null while stopped.</summary>
    public ImageSource? QrImage
    {
        get => _qrImage;
        private set => SetProperty(ref _qrImage, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Where received files land; shown next to the folder button.</summary>
    public string SaveDirectoryText
    {
        get => _saveDirectoryText;
        private set => SetProperty(ref _saveDirectoryText, value);
    }

    /// <summary>
    /// Starts the receiver. Files land in the transfer directory (custom output directory when
    /// configured, otherwise the Desktop itself). A first-run Windows firewall prompt is
    /// expected — allowing it once is what lets the phone reach this machine.
    /// </summary>
    private void Start()
    {
        if (IsRunning) return;

        string directory = OutputDirectoryResolver.Resolve(_settings.TransferOutputDirectory, AppFolders.Root);
        _targetDirectory = directory;
        try
        {
            _server = new LanTransferServer(trace: message => _trace(message));
            _server.FileReceived += OnServerFileReceived;
            _server.TextReceived += OnServerTextReceived;
            _server.PageOpened += OnServerPageOpened;
            _server.UploadStarted += OnServerUploadStarted;
            _server.UploadProgress += OnServerUploadProgress;
            _server.UploadFailed += OnServerUploadFailed;
            _server.ChatMessageAdded += OnChatMessageAdded;
            _server.Start(directory);
        }
        catch (Exception ex)
        {
            AppLog.Error("手机传文件：接收服务启动失败", ex);
            _server?.Dispose();
            _server = null;
            StatusText = $"接收服务启动失败：{ex.Message}";
            return;
        }

        SaveDirectoryText = directory;
        UrlText = _server.Url;
        QrImage = BuildQr(_server.Url);
        _receivedCount = 0;
        _receivedBytes = 0;
        Chat.Clear();
        IsConnected = false;
        IsRunning = true;
        StatusText = "等待手机扫码…（首次使用时若弹出防火墙提示，请选择「允许访问」）";
        AppLog.Info($"手机传文件：接收服务已启动 {_server.Url}");
    }

    public void Stop()
    {
        if (_server is not { } server) return;

        _server = null;
        server.FileReceived -= OnServerFileReceived;
        server.TextReceived -= OnServerTextReceived;
        server.PageOpened -= OnServerPageOpened;
        server.UploadStarted -= OnServerUploadStarted;
        server.UploadProgress -= OnServerUploadProgress;
        server.UploadFailed -= OnServerUploadFailed;
        server.ChatMessageAdded -= OnChatMessageAdded;
        server.Dispose();

        QrImage = null;
        UrlText = string.Empty;
        IsConnected = false;
        Chat.Clear();
        IsRunning = false;
        StatusText = "接收已停止。";
        AppLog.Info("手机传文件：接收已停止");
    }

    /// <summary>Socket thread → owner; the tally here is atomic, the owner marshals the row.</summary>
    private void OnServerFileReceived(string path, long bytes)
    {
        Interlocked.Increment(ref _receivedCount);
        Interlocked.Add(ref _receivedBytes, bytes);
        StatusText = $"已接收 {_receivedCount} 个文件 · 共 {FormatSize(_receivedBytes)}，保存在 {SaveDirectoryText}";
        FileReceived?.Invoke(path, bytes);
    }

    /// <summary>Socket thread → UI thread via the dispatcher: the clipboard is STA-only and the
    /// message list binds on the UI context. Each text appends a chat-style row (sender, time,
    /// kind) instead of overwriting the previous one; a CamScanner share link additionally kicks
    /// off the auto-download chain.</summary>
    private void OnServerTextReceived(TextMessage message)
    {
        bool shareLink = CamScannerShareClient.TryExtractShareLink(message.Text, out string link);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(() =>
        {
            Messages.Insert(0, new ReceivedMessageViewModel(message, shareLink));
            OnPropertyChanged(nameof(HasMessages));
            string prefix = $"已收到 {message.SenderLabel} 的文本（{message.Text.Length} 字）";
            if (TryCopyToClipboard(message.Text, out Exception? copyError))
            {
                StatusText = $"{prefix}，已自动复制到剪贴板";
            }
            else
            {
                AppLog.Error("手机传文件：写入剪贴板失败", copyError);
                StatusText = $"{prefix}，写入剪贴板失败，可从列表手动复制";
            }
        });

        if (!shareLink) return;

        // The link's own chat row lives only on this side: the phone already shows the message
        // it sent, and a mirrored "电脑" row there would read as an echo.
        dispatcher.BeginInvoke(() =>
        {
            TransferChatItemViewModel row = AddLinkRow(link);
            StartShareDownload(link, row);
        });
    }

    /// <summary>
    /// A locally owned chat row (negative ids never collide with the server's sequence, and the
    /// phone's polling never sees it) that follows the share link through its download chain.
    /// </summary>
    private TransferChatItemViewModel AddLinkRow(string link)
    {
        var message = new TransferChatMessage(
            --_localRowId, TransferChatDirection.Computer, TransferChatKind.Link,
            link, "分享链接", 0, DateTime.Now);
        var row = new TransferChatItemViewModel(message, null, null, null, OpenPdfFromLink, CopyChatText);
        Chat.Add(row);
        return row;
    }

    /// <summary>
    /// Copies the received text with a couple of retries: another app can hold the clipboard for
    /// a few dozen milliseconds (CLIPBRD_E_CANT_OPEN) exactly while a phone message arrives.
    /// Only a final failure is reported; the waits total ~120 ms and only when copying fails.
    /// </summary>
    private static bool TryCopyToClipboard(string text, out Exception? error)
    {
        error = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                if (attempt == 2) break;
                Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>Background chain for a recognized CamScanner share link: resolve the short link →
    /// read the document info → pull every page preview → hand the pages to the gallery. Async
    /// void on purpose: fully detached from the socket loop, reporting through the chat row's
    /// own progress states (plus the status line and log).</summary>
    private async void StartShareDownload(string link, TransferChatItemViewModel row)
    {
        try
        {
            var client = new CamScannerShareClient();
            var (eid, sid) = await client.ResolveAsync(link).ConfigureAwait(false);
            var info = await client.QueryAsync(eid, sid).ConfigureAwait(false);
            RunOnUi(() => row.ReportLinkTitle(info.Title));
            StatusText = $"正在下载「{info.Title}」（共 {info.PageFileNames.Count} 页）…";

            var pages = new List<byte[]>(info.PageFileNames.Count);
            for (var i = 0; i < info.PageFileNames.Count; i++)
            {
                pages.Add(await client.DownloadPageAsync(eid, sid, info.PageFileNames[i]).ConfigureAwait(false));
                int done = i + 1;
                double fraction = info.PageFileNames.Count == 0 ? 0 : (double)done / info.PageFileNames.Count;
                RunOnUi(() => row.ReportLinkStage($"正在下载第 {done}/{info.PageFileNames.Count} 页…", fraction));
                StatusText = $"正在下载「{info.Title}」第 {done}/{info.PageFileNames.Count} 页…";
            }

            // The old chain assembled every preview verbatim into a finished PDF — the
            // "mechanical" behaviour that made two ID sides two unrelated pages. Pages now land
            // in a gallery where the user pairs, cuts and packs them into one file.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                AppLog.Warn("手机传文件：没有 UI 线程，链接图片未进入拼版面板");
                RunOnUi(() => row.ReportLinkFailure("界面线程不可用，链接图片未加载"));
                return;
            }

            await dispatcher.InvokeAsync(() => LoadPendingPages(info.Title, pages));
            RunOnUi(() => row.ReportLinkReady($"已下载 {pages.Count} 张图片，可合并后处理"));
            StatusText = $"链接已下载 {pages.Count} 张图片，勾选后点击「合并生成 PDF」";
            AppLog.Info($"手机传文件：分享链接图片就绪 {pages.Count} 张（{info.Title}）");
        }
        catch (Exception ex)
        {
            AppLog.Error("手机传文件：分享链接下载失败", ex);
            RunOnUi(() => row.ReportLinkFailure($"下载失败：{ex.Message}"));
            StatusText = $"链接下载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// The link row's 去处理 PDF button: takes the link's document to the PDF page. A document
    /// already composed from this link goes as-is; otherwise the pending gallery is packed
    /// one-page-per-image (the generic default) and that file is what gets loaded. Re-entrancy
    /// guard: a second click while compose/naming is running must not fire a second hand-off
    /// (it used to re-navigate the user away from 手机传文件 mid-visit).
    /// </summary>
    private bool _openingPdf;

    private async void OpenPdfFromLink(TransferChatItemViewModel row)
    {
        if (_openingPdf) return;
        _openingPdf = true;
        try
        {
            string? pdf = _lastLinkPdf is { } existing && File.Exists(existing) ? existing : null;
            if (pdf is null && PendingPages.Count > 0)
            {
                row.ReportLinkStage("正在合并图片…", -1);
                await ComposePendingAsync(ComposeLayoutCatalog.Single, CropModeCatalog.All[2])
                    .ConfigureAwait(true);
                pdf = _lastLinkPdf;
            }

            PdfPageRequested?.Invoke(pdf);
        }
        finally
        {
            _openingPdf = false;
        }
    }

    /// <summary>Shows a freshly downloaded link in the gallery: all pages selected, a sensible
    /// pack pre-chosen, preview rendered. Runs on the UI thread (dispatcher-marshalled).</summary>
    private void LoadPendingPages(string title, IReadOnlyList<byte[]> pages)
    {
        foreach (ComposeSourceViewModel old in PendingPages)
        {
            old.SelectionChanged -= OnSourceSelectionChanged;
            old.Rotated -= OnSourceRotated;
        }
        PendingPages.Clear();
        _pendingTitle = string.IsNullOrWhiteSpace(title) ? "分享文档" : title;
        _lastLinkPdf = null;

        for (int i = 0; i < pages.Count; i++)
        {
            var item = new ComposeSourceViewModel(i + 1, pages[i], $"第 {i + 1} 张");
            item.SelectionChanged += OnSourceSelectionChanged;
            item.Rotated += OnSourceRotated;
            PendingPages.Add(item);
        }

        SelectedLayout = ComposeLayoutCatalog.Suggest(
            PendingPages.Select(p => (p.Width, p.Height)).ToList(),
            _pendingTitle.Contains("身份证", StringComparison.Ordinal) ||
            _pendingTitle.Contains("身份証", StringComparison.Ordinal));
        OnPropertyChanged(nameof(HasPendingPages));
        RaiseComposeState();
        RefreshPreview();

        // ID cards are a fixed, fully determined output: compose without waiting for a click.
        // The OCR confirmation inside keeps a card-shaped licence pair from being forced into
        // the ID sheet — those stay in the gallery for the user to pack manually.
        if (SelectedLayout.Kind == ComposeLayoutKind.IdCardA4 && _idCardOcr is not null)
        {
            StatusText = "检测到身份证，正在自动识别姓名与正反面…";
            _ = ComposePendingAsync(ComposeLayoutCatalog.IdCardA4, SelectedCropMode, autoConfirmed: true);
        }
    }

    private void ClearPending()
    {
        foreach (ComposeSourceViewModel item in PendingPages)
        {
            item.SelectionChanged -= OnSourceSelectionChanged;
            item.Rotated -= OnSourceRotated;
        }

        PendingPages.Clear();
        _pendingTitle = string.Empty;
        ComposePreview = null;
        OnPropertyChanged(nameof(HasPendingPages));
        RaiseComposeState();
    }

    private void OnSourceSelectionChanged(object? sender, EventArgs e)
    {
        RaiseComposeState();
        RefreshPreview();
    }

    private void OnSourceRotated(object? sender, EventArgs e) => RefreshPreview();

    /// <summary>Moves one gallery row after a drag; labels and the preview follow the new order.</summary>
    private void MovePendingPage(int from, int to)
    {
        if (from == to || from < 0 || to < 0 ||
            from >= PendingPages.Count || to >= PendingPages.Count)
        {
            return;
        }

        PendingPages.Move(from, to);
        RenumberPendingPages();
        RefreshPreview();
        StatusText = "已调整图片顺序，勾选与预览已同步更新。";
    }

    private void RenumberPendingPages()
    {
        for (var i = 0; i < PendingPages.Count; i++)
        {
            PendingPages[i].SetLabel($"第 {i + 1} 张");
        }
    }

    private void RaiseComposeState()
    {
        OnPropertyChanged(nameof(SelectedPageCount));
        ComposeCommand.RaiseCanExecuteChanged();
        ComposeOriginalCommand.RaiseCanExecuteChanged();
        ClearPendingCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Renders the first page of the current selection/pack/crop as a small JPEG so the
    /// user sees the actual cut before committing. Small enough to stay synchronous on the UI
    /// thread; failures just hide the preview.</summary>
    private void RefreshPreview()
    {
        byte[][] selected = SelectedBytes();
        if (selected.Length == 0)
        {
            ComposePreview = null;
            return;
        }

        try
        {
            byte[] page = ImagePageComposer.ComposePage(
                selected.Take(SelectedLayout.Capacity).ToArray(),
                SelectedLayout, new CropSettings(SelectedCropMode.Mode), 760);
            ComposePreview = ToImage(page);
        }
        catch (Exception ex)
        {
            AppLog.Trace("手机传文件：拼版预览失败", ex);
            ComposePreview = null;
        }
    }

    /// <summary>The checked images, in gallery order.</summary>
    private byte[][] SelectedBytes() => PendingPages
        .Where(p => p.IsSelected)
        .Select(p => p.Bytes)
        .ToArray();

    /// <summary>Composes the checked images (a page per pack capacity) into one PDF and hands it
    /// to the owner, which files it as a normal record with content naming.</summary>
    private async Task ComposePendingAsync(
        ComposeLayout layout,
        CropModeChoice crop,
        bool autoConfirmed = false)
    {
        List<ComposeSourceViewModel> chosen = PendingPages.Where(p => p.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            StatusText = "请先勾选要合并的图片";
            return;
        }
        if (IsComposing) return;

        IsComposing = true;
        StatusText = $"正在拼版（{chosen.Count} 张 · {layout.Name}）…";
        try
        {
            byte[][] selected = chosen.Select(p => p.Bytes).ToArray();
            string? personName = null;
            IdCardPlan? plan = null;
            if (layout.Kind == ComposeLayoutKind.IdCardA4 && _idCardOcr is not null)
            {
                StatusText = "正在识别身份证姓名与正反面…";
                try
                {
                    plan = await IdCardInspector
                        .PrepareAsync(selected, _idCardOcr)
                        .ConfigureAwait(true);
                    selected = plan.OrderedImages.ToArray();
                }
                catch (Exception ex)
                {
                    // Ordering/naming is a convenience; an OCR failure must still produce the file.
                    AppLog.Warn($"身份证识别失败，保留原顺序与标题：{ex.Message}");
                }
            }

            // The clickless path must not silently bake a non-ID pair into the ID template.
            if (autoConfirmed && plan is not { LooksLikeIdCard: true })
            {
                StatusText = "未能确认是身份证，已保留图片：请选择排版后手动合并。";
                return;
            }
            personName = plan?.Name;

            string title = personName is not null
                ? personName + "身份证"
                : string.IsNullOrWhiteSpace(_pendingTitle)
                    ? layout.Kind == ComposeLayoutKind.IdCardA4 ? "身份证" : "分享文档"
                    : _pendingTitle;
            var cropSettings = new CropSettings(crop.Mode);
            StatusText = $"正在拼版（{chosen.Count} 张 · {layout.Name}）…";

            IReadOnlyList<byte[]> pages = await Task.Run(
                () => ImagePageComposer.ComposePages(selected, layout, cropSettings)).ConfigureAwait(true);
            byte[] pdf = JpegPdfAssembler.Build(title, pages);

            string directory = _targetDirectory
                ?? OutputDirectoryResolver.Resolve(_settings.TransferOutputDirectory, AppFolders.Root);
            string suggestedName = CamScannerShareClient.SuggestFileName(
                new CamScannerShareClient.ShareDocumentInfo(title, Array.Empty<string>()));
            string target = OutputNameGenerator.FindAvailablePath(directory, suggestedName);
            await File.WriteAllBytesAsync(target, pdf).ConfigureAwait(true);
            _lastLinkPdf = target;

            // Only the checked pages leave the gallery: a mixed link (执照 + 身份证 + 户口本)
            // is processed in several passes, each pass producing its own file.
            RemoveSources(chosen);
            string remainder = PendingPages.Count == 0
                ? string.Empty
                : $"，还剩 {PendingPages.Count} 张未处理";
            StatusText = personName is not null
                ? $"已生成 {personName}身份证（{pages.Count} 页）：{Path.GetFileName(target)}{remainder}"
                : $"已把 {chosen.Count} 张图片合并为 {pages.Count} 页 PDF：{Path.GetFileName(target)}{remainder}";
            AppLog.Info($"手机传文件：链接图片拼版 {chosen.Count} 张 → {pages.Count} 页 {target}");
            ShareDocumentReceived?.Invoke(target, layout.Kind != ComposeLayoutKind.IdCardA4);
        }
        catch (Exception ex)
        {
            AppLog.Error("手机传文件：链接图片拼版失败", ex);
            StatusText = $"拼版失败：{ex.Message}";
        }
        finally
        {
            IsComposing = false;
        }
    }

    /// <summary>Drops the pages that just went into a file, keeping the rest of the link in the
    /// gallery for the next pass.</summary>
    private void RemoveSources(IReadOnlyList<ComposeSourceViewModel> sources)
    {
        foreach (ComposeSourceViewModel item in sources)
        {
            item.SelectionChanged -= OnSourceSelectionChanged;
            item.Rotated -= OnSourceRotated;
            PendingPages.Remove(item);
        }
        if (PendingPages.Count == 0) _pendingTitle = string.Empty;
        RenumberPendingPages();
        OnPropertyChanged(nameof(HasPendingPages));
        RaiseComposeState();
        RefreshPreview();
    }

    private static ImageSource? ToImage(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // 手机发来的可能是 WebP/HEIC：WPF 没有对应编解码器，SkiaSharp 兜底。
            try
            {
                using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
                return bitmap is null ? null : SkiaBitmapBridge.ToBitmapSource(bitmap);
            }
            catch
            {
                return null;
            }
        }
    }

    // StatusText updates from socket threads are fine: WPF marshals scalar PropertyChanged to
    // the UI thread automatically (same as OnServerFileReceived above).

    private void OnServerPageOpened() =>
        RunOnUi(() =>
        {
            IsConnected = true;
            StatusText = $"手机已打开传输页（{DateTime.Now:HH:mm:ss}），可双向发送文字、图片和文件";
        });

    /// <summary>Either direction added a chat row: append it on the UI thread.</summary>
    private void OnChatMessageAdded(TransferChatMessage message) =>
        RunOnUi(() =>
        {
            string? path = _server?.LocalPathFor(message.Id);
            Chat.Add(new TransferChatItemViewModel(message, path, OpenChatFile, RevealChatFile, CopyChatText));
        });

    /// <summary>Copies a chat bubble's text (text and share-link bubbles) back to the clipboard,
    /// reusing the same retry as the receive path: a browser or IM can briefly hold the clipboard.</summary>
    private void CopyChatText(TransferChatItemViewModel row)
    {
        if (TryCopyToClipboard(row.Text, out Exception? error))
            StatusText = "已复制到剪贴板";
        else
        {
            AppLog.Error("手机传文件：复制聊天文字失败", error);
            StatusText = "复制失败，请稍后再试";
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else _ = dispatcher.BeginInvoke(action);
    }

    /// <summary>Computer → phone text; the server echoes it back through the chat event.</summary>
    private void SendText()
    {
        if (_server is null) return;
        _server.SendText(DraftText);
        DraftText = string.Empty;
    }

    /// <summary>Sends a picked file/image to the phone (copied into the session outbox).</summary>
    public async Task SendFileAsync(string path)
    {
        if (_server is not { } server) return;
        try
        {
            await server.SendFileAsync(path).ConfigureAwait(true);
            StatusText = $"已发送 {Path.GetFileName(path)}；手机上点开或下载即可保存";
        }
        catch (Exception ex)
        {
            StatusText = $"发送失败：{ex.Message}";
        }
    }

    /// <summary>Files dropped onto the transfer card go straight to the phone.</summary>
    public async Task SendDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (!IsRunning)
        {
            StatusText = "请先点击「开始接收」并让手机扫码，再拖入文件";
            return;
        }

        string[] files = paths.Where(File.Exists).ToArray();
        if (files.Length == 0) return;

        foreach (string file in files) await SendFileAsync(file).ConfigureAwait(true);
        StatusText = $"已发送 {files.Length} 个文件到手机";
    }

    /// <summary>Clicking a file bubble opens it: images in the shared viewer, others via the shell.</summary>
    private void OpenChatFile(TransferChatItemViewModel row)
    {
        if (row.LocalPath is not { } path || !File.Exists(path))
        {
            StatusText = "文件已不存在，可能已被移动或删除";
            return;
        }

        if (row.IsImage)
        {
            OpenImageRequested?.Invoke(this, path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
        }
    }

    private void RevealChatFile(TransferChatItemViewModel row)
    {
        if (row.Directory.Length == 0) return;
        if (!_shell.TryOpenDirectory(row.Directory, out string? error))
            StatusText = $"打开文件夹失败：{error}";
    }

    private void OnServerUploadStarted(long total) =>
        StatusText = $"正在接收上传（共 {FormatSize(total)}）…";

    private void OnServerUploadProgress(long received, long total) =>
        StatusText = total > 0
            ? $"正在接收上传… {received * 100 / total}%（{FormatSize(received)} / {FormatSize(total)}）"
            : "正在接收上传…";

    private void OnServerUploadFailed(string reason) =>
        StatusText = $"传输中断：{reason}";

    private static ImageSource? BuildQr(string content)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
            using var png = new QRCoder.PngByteQRCode(data);
            byte[] bytes = png.GetGraphic(pixelsPerModule: 7);

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze(); // built off the UI thread or rebound later — frozen is always safe
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Error("手机传文件：二维码生成失败", ex);
            return null;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:F2} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
            _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    public void Dispose() => Stop();
}

/// <summary>One row in the received-text list: immutable content plus the chat-style metadata
/// (sender, server time, kind). 「复制」 puts the full text on the clipboard.</summary>
public sealed class ReceivedMessageViewModel
{
    public ReceivedMessageViewModel(TextMessage message, bool isShareLink)
    {
        TimeText = message.ReceivedAt.ToString("HH:mm:ss");
        Sender = message.SenderLabel;
        IsShareLink = isShareLink;
        FullText = message.Text;
        Preview = BuildPreview(message.Text);
        CopyCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(FullText); }
            catch (Exception ex) { AppLog.Error("手机传文件：复制文本失败", ex); }
        });
    }

    public string TimeText { get; }

    public string Sender { get; }

    /// <summary>「链接」 when the text carries a CamScanner share link, 「文本」 otherwise.</summary>
    public string KindText => IsShareLink ? "链接" : "文本";

    public bool IsShareLink { get; }

    /// <summary>First non-empty line, clipped — the whole text stays one row tall in the list.</summary>
    public string Preview { get; }

    public string FullText { get; }

    public RelayCommand CopyCommand { get; }

    private static string BuildPreview(string text)
    {
        var line = text.Split('\n').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0) ?? string.Empty;
        return line.Length <= 80 ? line : line[..80] + "…";
    }
}
