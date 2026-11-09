using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App;

/// <summary>
/// Embedded-browser login for 天翼云盘. The real page owns captcha/SMS/QR; this window harvests
/// the session cookies once the authenticated API answers — a cookie-name check alone proved
/// unreliable, so success is decided by actually calling listFiles with the current cookie.
/// Nothing is persisted here; the caller encrypts the header into the account (DPAPI).
/// </summary>
public partial class TianyiLoginWindow : Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private DispatcherTimer? _timer;
    private bool _completed;
    private bool _harvesting;
    private DateTime _lastValidateUtc = DateTime.MinValue;
    private string _lastCookieSignature = string.Empty;
    private string _lastApiRequest = string.Empty;

    public TianyiLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _timer?.Stop();
    }

    /// <summary>Cookie header (name=value; …) harvested after a successful login; null when cancelled.</summary>
    public string? CookieHeader { get; private set; }

    /// <summary>Current status line (also used by diagnostics probes).</summary>
    public string StatusText => StatusTextBlock.Text;

    /// <summary>Last harvest snapshot (cookie names, session-key availability, page API call).</summary>
    public string LastDiagnostics { get; private set; } = string.Empty;

    private async Task InitializeAsync()
    {
        try
        {
            // A stable folder under %LOCALAPPDATA% keeps the login session out of the versioned
            // install directory (users stay signed in across app updates).
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WatermarkRemover", "webview2");
            Directory.CreateDirectory(dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
                AppLog.Info($"天翼登录页导航：url={Browser.Source} success={args.IsSuccess}");
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
                AppLog.Info($"天翼登录页请求新窗口：{args.Uri}");

            // Diagnostic hook: the page's own API calls reveal the exact auth shape the private
            // API expects (cookies vs. a sessionKey query parameter). Kept as the last request so
            // a probe can compare it with the header this window synthesizes.
            Browser.CoreWebView2.AddWebResourceRequestedFilter(
                "https://cloud.189.cn/api/*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                var headerNames = args.Request.Headers
                    .Select(h => h.Key)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                _lastApiRequest = $"{args.Request.Method} {args.Request.Uri} headers=[{string.Join(",", headerNames)}]";
            };

            Browser.CoreWebView2.Navigate("https://cloud.189.cn/web/main");
            AppLog.Info($"天翼登录窗口已打开，WebView2 数据目录：{dataFolder}");

            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += async (_, _) => await TryHarvestAsync(manual: false);
            _timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("无法启动天翼云盘内置浏览器", ex);
            MessageBox.Show(this,
                "无法启动内置浏览器（WebView2）。请确认系统已安装 Microsoft Edge WebView2 运行时后重试。\n\n" + ex.Message,
                "天翼云盘登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
        }
    }

    private async Task TryHarvestAsync(bool manual)
    {
        if (_completed || _harvesting || Browser.CoreWebView2 is null)
        {
            return;
        }

        var source = Browser.Source?.ToString() ?? string.Empty;
        if (!manual && !source.Contains("cloud.189.cn", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _harvesting = true;
        try
        {
            await HarvestAsync(manual, source).ConfigureAwait(true);
        }
        finally
        {
            _harvesting = false;
        }
    }

    private async Task HarvestAsync(bool manual, string source)
    {
        List<CoreWebView2Cookie> cookies;
        try
        {
            cookies = await Browser.CoreWebView2!.CookieManager.GetCookiesAsync("https://cloud.189.cn");
            if (cookies.Count == 0 &&
                Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) &&
                sourceUri.Scheme is "http" or "https")
            {
                cookies = await Browser.CoreWebView2!.CookieManager.GetCookiesAsync(sourceUri.ToString());
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"天翼登录读取 Cookie 失败：{ex.Message}");
            SetStatus($"读取浏览器 Cookie 失败：{ex.Message}");
            return;
        }

        var usable = cookies.Where(c => !string.IsNullOrEmpty(c.Value)).ToList();
        var names = string.Join(",", usable.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        var changed = names != _lastCookieSignature;
        if (manual || changed)
        {
            _lastCookieSignature = names;
            AppLog.Info($"天翼登录检测：url={source}；Cookie 数量={usable.Count}；名称=[{names}]");
        }

        if (usable.Count == 0)
        {
            SetStatus("还没有检测到登录 Cookie，请先在页面里完成登录。");
            return;
        }

        // The timer polls while the user is still clicking around the page; validating on every
        // tick would pile up requests. Re-check only when the cookie set changed or a moment has
        // passed (the server can need a few seconds to accept a fresh login).
        if (!manual && !changed && DateTime.UtcNow - _lastValidateUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastValidateUtc = DateTime.UtcNow;

        var header = string.Join("; ", usable.Select(c => $"{c.Name}={c.Value}"));

        // The current web app keeps the API session key in sessionStorage ("sessionKey") instead
        // of writing a SESSION_KEY cookie. Try the plain cookie first (the historical shape) and
        // fall back to a header that carries the session key as a cookie.
        var sessionKey = await TryReadSessionKeyAsync().ConfigureAwait(true);
        var candidates = new List<(string Label, string Header)> { ("Cookie", header) };
        if (sessionKey is { Length: > 0 } && usable.All(c => c.Name != "SESSION_KEY"))
        {
            candidates.Add(("Cookie+SESSION_KEY", $"SESSION_KEY={sessionKey}; {header}"));
        }
        else if (sessionKey is null or { Length: 0 })
        {
            AppLog.Warn("天翼登录：页面 sessionStorage 中没有 sessionKey");
        }

        string? verdict = null;
        string? accepted = null;
        foreach (var candidate in candidates)
        {
            verdict = await ValidateCookieAsync(candidate.Header).ConfigureAwait(true);
            if (verdict is null)
            {
                accepted = candidate.Header;
                AppLog.Info($"天翼登录校验通过（{candidate.Label}）");
                break;
            }
        }

        LastDiagnostics =
            $"url={source}; cookies=[{names}]; sessionKey=" +
            (sessionKey is { Length: > 0 } key ? $"len={key.Length}" : "none") +
            $"; verdict={verdict ?? "ok"}; pageApi=[{_lastApiRequest}]";

        if (accepted is not null)
        {
            _completed = true;
            _timer?.Stop();
            CookieHeader = accepted;
            AppLog.Info($"天翼登录成功：已获取 {usable.Count} 个 Cookie 并写入账号");
            DialogResult = true;
            return;
        }

        AppLog.Info($"天翼登录校验未通过：{verdict}；页面 API 示例：{_lastApiRequest}");
        SetStatus(verdict ?? "登录状态尚未生效，请稍候。");
    }

    /// <summary>Null = authenticated; otherwise a user-facing reason to keep waiting.</summary>
    private static async Task<string?> ValidateCookieAsync(string cookieHeader)
    {
        try
        {
            using var http = new HttpClient(TianyiHttp.CreateHandler())
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://cloud.189.cn/api/open/file/listFiles.action" +
                $"?noCache=0.{Random.Shared.Next(1, 100000)}&pageSize=1&pageNum=1&mediaType=0" +
                "&folderId=-11&iconOption=5&orderBy=lastOpTime&descending=true");
            request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
            request.Headers.TryAddWithoutValidation("Referer", "https://cloud.189.cn/");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorCode", out var errorCode) &&
                errorCode.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(errorCode.GetString()))
            {
                var message = root.TryGetProperty("errorMsg", out var errorMsg) ? errorMsg.GetString() : null;
                return $"Cookie 尚未生效（{message ?? errorCode.GetString()}），请确认已登录成功。";
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"天翼登录校验请求失败：{ex.Message}");
            return $"校验登录状态失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Reads the API session key that the current web app keeps in sessionStorage. Its private API
    /// still authenticates through a SESSION_KEY cookie, but the page stopped writing that cookie,
    /// so the value has to be lifted out of the page and re-injected into the harvested header.
    /// </summary>
    private async Task<string?> TryReadSessionKeyAsync()
    {
        if (Browser.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{return sessionStorage.getItem('sessionKey')||localStorage.getItem('sessionKey')||'';}catch(e){return '';}})()");
            if (string.IsNullOrEmpty(json) || json == "null")
            {
                return null;
            }

            var value = JsonSerializer.Deserialize<string>(json);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"天翼登录读取 sessionStorage 失败：{ex.Message}");
            return null;
        }
    }

    private void SetStatus(string text) => StatusTextBlock.Text = text;

    private async void OnHarvestClick(object sender, RoutedEventArgs e) =>
        await TryHarvestAsync(manual: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
