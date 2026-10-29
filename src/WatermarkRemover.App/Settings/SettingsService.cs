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

    public static AppSettings Load() => Load(SettingsPath);

    /// <summary>Loads from an explicit path (tests) and migrates an older schema in memory.</summary>
    internal static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();

            string text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(text, Options) ?? new AppSettings();
            Migrate(loaded, ReadSchemaVersion(text), path);
            return loaded;
        }
        catch (Exception ex)
        {
            // A corrupt settings file must never block the app from starting.
            AppLog.Error($"设置文件不可读，已重置为默认：{path}", ex);
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings) => Save(settings, SettingsPath);

    internal static bool Save(AppSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            settings.SchemaVersion = SettingsSchema.CurrentVersion;
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"设置保存失败：{path}", ex);
            return false;
        }
    }

    /// <summary>
    /// Version stamp of a settings document: the property when present, 1 for the legacy
    /// unversioned files. Tolerant of either casing because hand-edited files appear in support.
    /// </summary>
    private static int ReadSchemaVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.TryGetInt32(out int version))
                {
                    return version;
                }
            }
        }
        catch (JsonException)
        {
            // Falls through to "legacy": the deserialise above already reported a bad file.
        }

        return 1;
    }

    /// <summary>
    /// Brings an older document up to <see cref="SettingsSchema.CurrentVersion"/>. There are no
    /// value rewrites yet (v1→v2 only adds the version stamp), but this is the one place a future
    /// release adds them — a switch arm per step keeps the chain explicit.
    /// </summary>
    private static void Migrate(AppSettings settings, int fromVersion, string path)
    {
        if (fromVersion >= SettingsSchema.CurrentVersion)
        {
            if (fromVersion > SettingsSchema.CurrentVersion)
            {
                AppLog.Warn($"设置文件来自更新的版本（schema {fromVersion}），按 schema " +
                            $"{SettingsSchema.CurrentVersion} 读取，未识别的字段会被忽略。");
            }

            settings.SchemaVersion = SettingsSchema.CurrentVersion;
            return;
        }

        AppLog.Info($"设置文件 schema 迁移：{fromVersion} → {SettingsSchema.CurrentVersion}（{path}）");
        settings.SchemaVersion = SettingsSchema.CurrentVersion;
    }
}
