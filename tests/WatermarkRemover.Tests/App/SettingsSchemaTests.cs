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
}
