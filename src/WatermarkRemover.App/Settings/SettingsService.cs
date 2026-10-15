using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. The file lives next to the executable when
/// that directory is writable (portable use), otherwise under %APPDATA%\WatermarkRemover — see
/// <see cref="AppFolders"/>.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsPath => AppFolders.SettingsFilePath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // A corrupt settings file must never block the app from starting.
            AppLog.Error($"设置文件不可读，已重置为默认：{SettingsPath}", ex);
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"设置保存失败：{SettingsPath}", ex);
            return false;
        }
    }
}
