using System.IO;
using System.Text.Json;

namespace WatermarkRemover.App.Services;

public sealed record AutomaticBackupState(
    long Length,
    DateTimeOffset LastWriteUtc,
    string Sha256,
    DateTimeOffset LastSuccessUtc);

/// <summary>Per-path upload state. Atomic replacement keeps a crash from producing a partial file.</summary>
public sealed class AutomaticBackupStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private Dictionary<string, AutomaticBackupState> _states = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public AutomaticBackupStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public bool TryGet(
        string configurationFingerprint,
        string relativePath,
        out AutomaticBackupState? state)
    {
        string key = MakeKey(configurationFingerprint, relativePath);
        lock (_sync)
        {
            EnsureLoadedLocked();
            return _states.TryGetValue(key, out state) && IsUsable(state);
        }
    }

    public bool Upsert(
        string configurationFingerprint,
        string relativePath,
        AutomaticBackupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string key = MakeKey(configurationFingerprint, relativePath);
        lock (_sync)
        {
            EnsureLoadedLocked();
            var updated = new Dictionary<string, AutomaticBackupState>(_states, StringComparer.OrdinalIgnoreCase)
            {
                [key] = state,
            };
            if (!SaveLocked(updated)) return false;
            _states = updated;
            return true;
        }
    }

    public bool PruneMissing(string configurationFingerprint, IEnumerable<string> existingRelativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        ArgumentNullException.ThrowIfNull(existingRelativePaths);
        string fingerprint = NormalizeFingerprint(configurationFingerprint);
        string prefix = fingerprint + "\n";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in existingRelativePaths)
            existing.Add(AutomaticBackupFileFilter.NormalizeRelativePath(path));

        lock (_sync)
        {
            EnsureLoadedLocked();
            var updated = _states
                .Where(pair => !pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                               existing.Contains(pair.Key[prefix.Length..]))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (updated.Count == _states.Count) return true;
            if (!SaveLocked(updated)) return false;
            _states = updated;
            return true;
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AutomaticBackupState>>(
                File.ReadAllText(FilePath), Options);
            if (loaded is null) return;
            _states = loaded
                .Where(pair => pair.Value is not null && IsUsable(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A missing, damaged or unreadable state means "not uploaded", never "already safe".
            _states = new Dictionary<string, AutomaticBackupState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool SaveLocked(Dictionary<string, AutomaticBackupState> states)
    {
        string temporaryPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(states, Options));
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
                // Best-effort cleanup; the next save replaces the temporary file.
            }

            return false;
        }
    }

    private static string MakeKey(string configurationFingerprint, string relativePath) =>
        NormalizeFingerprint(configurationFingerprint) + "\n" +
        AutomaticBackupFileFilter.NormalizeRelativePath(relativePath);

    private static string NormalizeFingerprint(string configurationFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        return configurationFingerprint.Trim().ToLowerInvariant();
    }

    private static bool IsUsable(AutomaticBackupState? state) =>
        state is { Length: >= 0 } && !string.IsNullOrWhiteSpace(state.Sha256);
}
