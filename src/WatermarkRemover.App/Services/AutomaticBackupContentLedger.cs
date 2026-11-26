using System.IO;
using System.Text.Json;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Content ledger: every successfully uploaded file's SHA-256, keyed per backup configuration.
/// The per-path state answers "was THIS path uploaded"; this ledger answers "was this CONTENT
/// uploaded anywhere". It is what stops a moved file from being uploaded again: the front desk
/// moving a client folder into yesterday's folder changes the relative path, the old path state
/// is pruned, and without a content index the whole folder looked new.
/// </summary>
public sealed class AutomaticBackupContentLedger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly object _sync = new();
    // key = "<配置指纹>\n<sha256>" → 首次上传成功时间
    private Dictionary<string, DateTimeOffset> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public AutomaticBackupContentLedger(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    /// <summary>True when this exact content was uploaded before under this configuration.</summary>
    public bool TryGetLastSuccess(
        string configurationFingerprint, string sha256, out DateTimeOffset lastSuccessUtc)
    {
        string key = MakeKey(configurationFingerprint, sha256);
        lock (_sync)
        {
            EnsureLoadedLocked();
            return _entries.TryGetValue(key, out lastSuccessUtc);
        }
    }

    /// <summary>Records a successful upload (or a mirrored path). Best-effort: a failed ledger
    /// save must never turn a successful upload into a failed job.</summary>
    public void Record(string configurationFingerprint, string sha256, DateTimeOffset lastSuccessUtc)
    {
        string key = MakeKey(configurationFingerprint, sha256);
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_entries.TryGetValue(key, out DateTimeOffset existing) && existing >= lastSuccessUtc)
                return;

            var updated = new Dictionary<string, DateTimeOffset>(_entries, StringComparer.OrdinalIgnoreCase)
            {
                [key] = lastSuccessUtc,
            };
            if (SaveLocked(updated)) _entries = updated;
        }
    }

    /// <summary>
    /// Seeds the ledger from the existing per-path state so files uploaded before this feature
    /// existed are protected from the very next move, not only after another upload.
    /// </summary>
    public void Seed(IEnumerable<(string Fingerprint, string Sha256, DateTimeOffset LastSuccessUtc)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_sync)
        {
            EnsureLoadedLocked();
            var updated = new Dictionary<string, DateTimeOffset>(_entries, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach ((string fingerprint, string sha256, DateTimeOffset success) in items)
            {
                if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(sha256)) continue;
                string key = MakeKey(fingerprint, sha256);
                if (updated.TryGetValue(key, out DateTimeOffset existing) && existing >= success) continue;
                updated[key] = success;
                changed = true;
            }

            if (changed && SaveLocked(updated)) _entries = updated;
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                File.ReadAllText(FilePath), Options);
            if (loaded is null) return;
            _entries = new Dictionary<string, DateTimeOffset>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A damaged ledger only means "unknown", which falls back to path-based behaviour.
            _entries = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool SaveLocked(Dictionary<string, DateTimeOffset> entries)
    {
        string temporaryPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, Options));
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup.
            }

            return false;
        }
    }

    private static string MakeKey(string configurationFingerprint, string sha256) =>
        configurationFingerprint.Trim().ToLowerInvariant() + "\n" + sha256.Trim().ToLowerInvariant();
}
