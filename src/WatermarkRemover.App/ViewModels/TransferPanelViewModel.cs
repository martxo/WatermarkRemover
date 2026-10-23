using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Scan.Pdf;

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
    private LanTransferServer? _server;

    private bool _isRunning;
    private string _urlText = string.Empty;
    private ImageSource? _qrImage;
    private string _statusText = "点击「开始接收」，生成二维码后用手机扫码。";
    private string _saveDirectoryText = string.Empty;
    private int _receivedCount;
    private long _receivedBytes;
    private string? _targetDirectory;

    public TransferPanelViewModel(AppSettings settings, IShellOpenService shell, Action<string>? trace = null)
    {
        _settings = settings;
        _shell = shell;
        _trace = trace ?? (_ => { });

        ToggleCommand = new RelayCommand(() =>
        {
            if (IsRunning) Stop();
            else Start();
        });
        OpenFolderCommand = new RelayCommand(
            () => _shell.TryOpenDirectory(_saveDirectoryText, out _),
            () => IsRunning);
    }

    /// <summary>Relayed from the server; fires on a socket thread per landed file.</summary>
    public event Action<string, long>? FileReceived;

    /// <summary>A PDF assembled from a CamScanner share link is ready at this path; the owner
    /// (MainViewModel) routes it into the watermark-removal pipeline. Raised on a worker thread.</summary>
    public event Action<string>? ShareDocumentReceived;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Chat-style log of texts posted from the phone, newest first; each row keeps its
    /// sender, server-side time and kind, and copies its full text back on demand.</summary>
    public ObservableCollection<ReceivedMessageViewModel> Messages { get; } = new();

    public bool HasMessages => Messages.Count > 0;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                OnPropertyChanged(nameof(ToggleText));
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
        server.Dispose();

        QrImage = null;
        UrlText = string.Empty;
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
            try
            {
                Clipboard.SetText(message.Text);
                StatusText = $"已收到 {message.SenderLabel} 的文本（{message.Text.Length} 字），已自动复制到剪贴板";
            }
            catch (Exception ex)
            {
                AppLog.Error("手机传文件：写入剪贴板失败", ex);
                StatusText = $"已收到 {message.SenderLabel} 的文本（{message.Text.Length} 字），写入剪贴板失败，可从列表手动复制";
            }
        });

        if (shareLink) StartShareDownload(link);
    }

    /// <summary>Background chain for a recognized CamScanner share link: resolve the short link →
    /// read the document info → pull every page preview → assemble the PDF verbatim → hand the
    /// file to the owner, which routes it into the watermark-removal pipeline. Async void on
    /// purpose: fully detached from the socket loop, reporting only via status line and log.</summary>
    private async void StartShareDownload(string link)
    {
        try
        {
            var client = new CamScannerShareClient();
            var (eid, sid) = await client.ResolveAsync(link).ConfigureAwait(false);
            var info = await client.QueryAsync(eid, sid).ConfigureAwait(false);
            StatusText = $"正在下载「{info.Title}」（共 {info.PageFileNames.Count} 页）…";

            var pages = new List<byte[]>(info.PageFileNames.Count);
            for (var i = 0; i < info.PageFileNames.Count; i++)
            {
                pages.Add(await client.DownloadPageAsync(eid, sid, info.PageFileNames[i]).ConfigureAwait(false));
                StatusText = $"正在下载「{info.Title}」第 {i + 1}/{info.PageFileNames.Count} 页…";
            }

            byte[] pdf = JpegPdfAssembler.Build(info.Title, pages);

            string directory = _targetDirectory ?? Path.GetTempPath();
            string target = OutputNameGenerator.FindAvailablePath(directory, CamScannerShareClient.SuggestFileName(info));
            await File.WriteAllBytesAsync(target, pdf).ConfigureAwait(false);

            StatusText = $"链接转 PDF 完成（{info.PageFileNames.Count} 页），已保存到接收目录";
            AppLog.Info($"手机传文件：分享链接转 PDF {target}");
            ShareDocumentReceived?.Invoke(target);
        }
        catch (Exception ex)
        {
            AppLog.Error("手机传文件：分享链接转 PDF 失败", ex);
            StatusText = $"链接转 PDF 失败：{ex.Message}";
        }
    }

    // StatusText updates from socket threads are fine: WPF marshals scalar PropertyChanged to
    // the UI thread automatically (same as OnServerFileReceived above).

    private void OnServerPageOpened() =>
        StatusText = $"手机已打开传输页（{DateTime.Now:HH:mm:ss}），可在手机上选择文件或发送文本";

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
