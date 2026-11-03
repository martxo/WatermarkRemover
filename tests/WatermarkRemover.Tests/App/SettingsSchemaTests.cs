using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Settings carry a schema version so a future release can migrate an old file instead of
/// guessing what the old defaults used to mean. These tests pin the contract.
/// </summary>
public sealed class SettingsSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "WatermarkRemover.settings.json");

    [Fact]
    public void A_legacy_file_without_a_version_is_migrated_on_load()
    {
        // Exactly what releases before this field wrote: no SchemaVersion at all.
        File.WriteAllText(FilePath, "{ \"OutputSuffix\": \"_old\", \"MaxConcurrency\": 3 }");

        AppSettings settings = SettingsService.Load(FilePath);

        Assert.Equal(SettingsSchema.CurrentVersion, settings.SchemaVersion);
        Assert.Equal("_old", settings.OutputSuffix); // migration must not reset values
        Assert.Equal(3, settings.MaxConcurrency);
    }

    [Fact]
    public void Saving_writes_the_current_version_and_it_survives_a_reload()
    {
        var settings = new AppSettings { OutputSuffix = "_v2" };
        Assert.True(SettingsService.Save(settings, FilePath));

        string text = File.ReadAllText(FilePath);
        Assert.Contains($"\"SchemaVersion\": {SettingsSchema.CurrentVersion}", text);

        AppSettings reloaded = SettingsService.Load(FilePath);
        Assert.Equal(SettingsSchema.CurrentVersion, reloaded.SchemaVersion);
        Assert.Equal("_v2", reloaded.OutputSuffix);
    }

    [Fact]
    public void A_file_from_a_newer_release_still_loads()
    {
        File.WriteAllText(FilePath,
            $"{{ \"SchemaVersion\": {SettingsSchema.CurrentVersion + 5}, \"OutputSuffix\": \"_future\" }}");

        AppSettings settings = SettingsService.Load(FilePath);

        // Unknown future fields are ignored, known ones keep working: never refuse to start.
        Assert.Equal("_future", settings.OutputSuffix);
        Assert.Equal(SettingsSchema.CurrentVersion, settings.SchemaVersion);
    }

    // --- 1.16.81 新默认：只改代码默认，绝不静默覆盖已保存的选择 --------------------

    [Fact]
    public void A_fresh_install_gets_the_new_defaults()
    {
        var settings = new AppSettings();

        Assert.Equal("F1", settings.ScreenshotHotkey);              // 单键热键
        Assert.False(settings.NameFromFirstPageOnly);               // 全部识别（前 4 页）
        Assert.Equal(AppSettings.WeChatOcrEngineName, settings.PreferredOcrEngine);
        Assert.Equal(0, settings.DefaultsNoticeVersion);
    }

    [Fact]
    public void An_existing_file_keeps_its_saved_values_over_the_new_defaults()
    {
        // 本机 settings JSON 的实况：三项都是旧值且键存在。加载语义必须是「已有值赢」——
        // 新默认只对缺键/新安装生效，用户保存过的选择永不被悄悄改掉。
        File.WriteAllText(FilePath,
            "{ \"ScreenshotHotkey\": \"Ctrl+Alt+A\", \"NameFromFirstPageOnly\": true, " +
            "\"PreferredOcrEngine\": \"\" }");

        AppSettings settings = SettingsService.Load(FilePath);

        Assert.Equal("Ctrl+Alt+A", settings.ScreenshotHotkey);
        Assert.True(settings.NameFromFirstPageOnly);
        Assert.Equal(string.Empty, settings.PreferredOcrEngine);
    }

    [Fact]
    public void A_file_missing_the_keys_picks_up_the_new_defaults()
    {
        // 缺键 = 没表达过偏好：用当前版本的默认值补齐。
        File.WriteAllText(FilePath, "{ \"OutputSuffix\": \"_keep\" }");

        AppSettings settings = SettingsService.Load(FilePath);

        Assert.Equal("_keep", settings.OutputSuffix);   // 已有的照样保留
        Assert.Equal("F1", settings.ScreenshotHotkey);
        Assert.False(settings.NameFromFirstPageOnly);
        Assert.Equal(AppSettings.WeChatOcrEngineName, settings.PreferredOcrEngine);
    }

    [Fact]
    public void The_wechat_default_matches_the_engine_chain_factory_name()
    {
        // Core 里的默认引擎名必须和 App 引擎链里注册的工厂名一致，否则默认值会落空。
        Assert.Contains(AppSettings.WeChatOcrEngineName,
            AppOcrEngineChain.Instance.Factories.Select(f => f.Name));
    }
}
