using System.IO;
using System.Security.Cryptography;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>Authorized, per-path automatic backup with stable-file snapshots and serial uploads.</summary>
public sealed class AutomaticBackupService : ObservableObject, IDisposable
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(30);
    private readonly AutomaticBackupStateStore _stateStore;
    private readonly string _snapshotDirectory;
    private readonly Func<NetdiskUploadTarget, string, string, CancellationToken, Task> _uploadAsync;
    private readonly TimeSpan _stableWindow;
    private readonly TimeSpan _rescanInterval;
    private readonly TimeSpan _initialRetryDelay;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private Generation? _generation;
    private int _shutdown;
    private bool _isRunning;
    private int _pendingCount;
    private DateTimeOffset? _lastSuccessUtc;
    private string? _lastError;

    public AutomaticBackupService(
        NetdiskTransferService transfer,
        AutomaticBackupStateStore stateStore,
        string snapshotDirectory)
        : this(
            stateStore,
            snapshotDirectory,
            CreateUploadAdapter(transfer),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(30))
    {
    }

    internal AutomaticBackupService(
        AutomaticBackupStateStore stateStore,
        string snapshotDirectory,
        Func<NetdiskUploadTarget, string, string, CancellationToken, Task> uploadAsync,
        TimeSpan stableWindow,
        TimeSpan rescanInterval,
        TimeSpan retryInterval)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        _snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        _uploadAsync = uploadAsync ?? throw new ArgumentNullException(nameof(uploadAsync));
        if (stableWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stableWindow));
        if (rescanInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rescanInterval));
        if (retryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryInterval));
        _stableWindow = stableWindow;
        _rescanInterval = rescanInterval;
        _initialRetryDelay = retryInterval;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public int PendingCount
    {
        get => _pendingCount;
        private set => SetProperty(ref _pendingCount, value);
    }

    public DateTimeOffset? LastSuccessUtc
    {
        get => _lastSuccessUtc;
        private set => SetProperty(ref _lastSuccessUtc, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public async Task ApplyConfigurationAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _configurationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _shutdown) != 0) return;
            await StopCurrentGenerationAsync().ConfigureAwait(false);
            LastError = null;
            if (!AutomaticBackupFileFilter.HasValidConsent(settings)) return;

            string root = AutomaticBackupFileFilter.NormalizeRoot(settings.AutomaticBackupRootDirectory);
            string fingerprint = AutomaticBackupFileFilter.ConsentFingerprint(settings);
            var target = new NetdiskUploadTarget(
                settings.AutomaticBackupProviderId.Trim(),
                settings.AutomaticBackupAccountName.Trim(),
                settings.AutomaticBackupDrive.Trim(),
                settings.AutomaticBackupRemoteDirectory.Trim());
            // Settings-driven windows, clamped; a missing/invalid value falls back to the
            // constructor defaults (which the tests inject with short values).
            TimeSpan stableWindow = settings.AutomaticBackupStableSeconds > 0
                ? TimeSpan.FromSeconds(Math.Clamp(settings.AutomaticBackupStableSeconds, 1, 3600))
                : _stableWindow;
            TimeSpan rescanInterval = settings.AutomaticBackupRescanMinutes > 0
                ? TimeSpan.FromMinutes(Math.Clamp(settings.AutomaticBackupRescanMinutes, 1, 1440))
                : _rescanInterval;
            var generation = new Generation(root, fingerprint, target, stableWindow, rescanInterval);
            try
            {
                // Enable events before the initial enumeration so a create/write cannot fall into the gap.
                generation.Watcher = CreateWatcher(generation);
                _generation = generation;
                generation.Worker = Task.Run(() => RunWorkerAsync(generation));
                IsRunning = true;
                Signal(generation);
            }
            catch (Exception ex)
            {
                generation.Deactivate();
                _generation = null;
                generation.Dispose();
                IsRunning = false;
                PendingCount = 0;
                LastError = ex.Message;
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public void Shutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        _configurationGate.Wait();
        try
        {
            StopCurrentGenerationAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public void Dispose() => Shutdown();

    private static Func<NetdiskUploadTarget, string, string, CancellationToken, Task> CreateUploadAdapter(
        NetdiskTransferService transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return (target, relativeDirectory, localPath, ct) => transfer.UploadFileAsync(
            new NetdiskUploadTarget(
                target.ProviderId,
                target.AccountName,
                target.DriveName,
                AutomaticBackupFileFilter.CombineRemoteDirectory(target.RemoteDirectory, relativeDirectory)),
            localPath,
            ct);
    }

    private FileSystemWatcher CreateWatcher(Generation generation)
    {
        var watcher = new FileSystemWatcher(generation.RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnFileObserved(generation, e.FullPath);
        watcher.Changed += (_, e) => OnFileObserved(generation, e.FullPath);
        watcher.Deleted += (_, e) => RemovePending(generation, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            RemovePending(generation, e.OldFullPath);
            OnFileObserved(generation, e.FullPath);
        };
        watcher.Error += (_, _) => ScheduleFullScan(generation);
        try
        {
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    private void OnFileObserved(Generation generation, string fullPath)
    {
        try
        {
            RegisterPending(generation, fullPath, fromWatcher: true);
        }
        catch
        {
            // FileSystemWatcher callbacks must never fault the process; the periodic scan retries.
            ScheduleFullScan(generation);
        }
    }

    private void RegisterPending(Generation generation, string fullPath, bool fromWatcher)
    {
        if (!generation.Active || IsSnapshotPath(fullPath)) return;
        if (!AutomaticBackupFileFilter.TryGetSupportedRelativePath(
                generation.RootDirectory, fullPath, out string relativePath))
            return;

        string normalizedFullPath = Path.GetFullPath(fullPath);
        bool hasMetadata = TryReadMetadata(normalizedFullPath, out long length, out DateTimeOffset lastWriteUtc);
        if (!hasMetadata && !File.Exists(normalizedFullPath))
        {
            RemovePendingByRelativePath(generation, relativePath);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int? pendingCount = null;
        lock (generation.Sync)
        {
            if (!generation.Active) return;
            if (!generation.Pending.TryGetValue(relativePath, out PendingFile? pending))
            {
                pending = new PendingFile(normalizedFullPath)
                {
                    Length = hasMetadata ? length : -1,
                    LastWriteUtc = lastWriteUtc,
                    NotBeforeUtc = now + generation.StableWindow,
                    NextAttemptUtc = now + generation.StableWindow,
                };
                generation.Pending.Add(relativePath, pending);
                pendingCount = generation.Pending.Count;
            }
            else if (hasMetadata && (pending.Length != length || pending.LastWriteUtc != lastWriteUtc))
            {
                pending.Length = length;
                pending.LastWriteUtc = lastWriteUtc;
                pending.NotBeforeUtc = now + generation.StableWindow;
                pending.NextAttemptUtc = pending.NotBeforeUtc;
                pending.Failures = 0;
            }
            else if (fromWatcher)
            {
                pending.NotBeforeUtc = now + generation.StableWindow;
                if (pending.NextAttemptUtc < pending.NotBeforeUtc)
                    pending.NextAttemptUtc = pending.NotBeforeUtc;
            }
        }

        if (pendingCount.HasValue) PendingCount = pendingCount.Value;
        Signal(generation);
    }

    private void RemovePending(Generation generation, string fullPath)
    {
        try
        {
            if (!AutomaticBackupFileFilter.TryGetRelativePath(
                    generation.RootDirectory, fullPath, out string relativePath))
                return;
            RemovePendingByRelativePath(generation, relativePath);
        }
        catch
        {
            // A malformed event path is ignored; a full scan reconciles real files.
        }
    }

    private void RemovePendingByRelativePath(Generation generation, string relativePath)
    {
        int? pendingCount = null;
        lock (generation.Sync)
        {
            if (generation.Pending.Remove(relativePath))
                pendingCount = generation.Pending.Count;
        }

        // The service updates this property from worker/watcher threads; WPF marshals scalar bindings.
        if (pendingCount.HasValue) PendingCount = pendingCount.Value;
    }

    private void RemovePendingIfUnchanged(
        Generation generation,
        string relativePath,
        long length,
        DateTimeOffset lastWriteUtc)
    {
        int? pendingCount = null;
        lock (generation.Sync)
        {
            if (!generation.Pending.TryGetValue(relativePath, out PendingFile? pending) ||
                pending.Length != length || pending.LastWriteUtc != lastWriteUtc)
                return;
            generation.Pending.Remove(relativePath);
            pendingCount = generation.Pending.Count;
        }

        if (pendingCount.HasValue) PendingCount = pendingCount.Value;
    }

    private void ScheduleFullScan(Generation generation)
    {
        if (!generation.Active) return;
        lock (generation.Sync)
            generation.NextFullScanUtc = DateTimeOffset.UtcNow;
        Signal(generation);
    }

    private static void Signal(Generation generation)
    {
        if (!generation.Active) return;
        try
        {
            if (generation.Signal.CurrentCount == 0) generation.Signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake-up already covers this change.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race with a watcher callback.
        }
    }

    private async Task RunWorkerAsync(Generation generation)
    {
        CancellationToken ct = generation.Cancellation.Token;
        while (!ct.IsCancellationRequested && generation.Active)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= generation.NextFullScanUtc)
                    await ScanAsync(generation, ct).ConfigureAwait(false);

                string? next = TakeNextReady(generation);
                if (next is not null)
                {
                    await ProcessPendingAsync(generation, next, ct).ConfigureAwait(false);
                    continue;
                }

                TimeSpan delay = GetWorkerDelay(generation);
                if (delay > TimeSpan.Zero)
                    await generation.Signal.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ScheduleFullScan(generation);
                try
                {
                    await Task.Delay(_initialRetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private Task ScanAsync(Generation generation, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (string path in Directory.EnumerateFiles(generation.RootDirectory, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!AutomaticBackupFileFilter.TryGetSupportedRelativePath(
                        generation.RootDirectory, path, out string relativePath))
                    continue;
                existing.Add(relativePath);
                RegisterPending(generation, path, fromWatcher: false);
            }

            _stateStore.PruneMissing(generation.ConfigurationFingerprint, existing);
            int pendingCount;
            lock (generation.Sync)
            {
                foreach (string relativePath in generation.Pending.Keys
                             .Where(path => !existing.Contains(path) &&
                                            !File.Exists(generation.Pending[path].FullPath))
                             .ToList())
                    generation.Pending.Remove(relativePath);
                pendingCount = generation.Pending.Count;
            }
            PendingCount = pendingCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            lock (generation.Sync)
            {
                if (!ct.IsCancellationRequested)
                    generation.NextFullScanUtc = DateTimeOffset.UtcNow + generation.RescanInterval;
            }
        }

        return Task.CompletedTask;
    }

    private static string? TakeNextReady(Generation generation)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (generation.Sync)
        {
            return generation.Pending
                .Where(pair => now >= pair.Value.NotBeforeUtc && now >= pair.Value.NextAttemptUtc)
                .OrderBy(pair => pair.Value.NextAttemptUtc)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .FirstOrDefault();
        }
    }

    private TimeSpan GetWorkerDelay(Generation generation)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset next = generation.NextFullScanUtc;
        lock (generation.Sync)
        {
            foreach (PendingFile pending in generation.Pending.Values)
            {
                DateTimeOffset due = pending.NotBeforeUtc > pending.NextAttemptUtc
                    ? pending.NotBeforeUtc
                    : pending.NextAttemptUtc;
                if (due < next) next = due;
            }
        }

        TimeSpan delay = next - now;
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private async Task ProcessPendingAsync(Generation generation, string relativePath, CancellationToken ct)
    {
        string? snapshotPath = null;
        string? snapshotAttemptDirectory = null;
        try
        {
            PendingFile pending;
            long observedLength;
            DateTimeOffset observedLastWriteUtc;
            DateTimeOffset notBeforeUtc;
            DateTimeOffset nextAttemptUtc;
            lock (generation.Sync)
            {
                if (!generation.Pending.TryGetValue(relativePath, out pending!)) return;
                observedLength = pending.Length;
                observedLastWriteUtc = pending.LastWriteUtc;
                notBeforeUtc = pending.NotBeforeUtc;
                nextAttemptUtc = pending.NextAttemptUtc;
            }

            if (!TryReadMetadata(pending.FullPath, out long length, out DateTimeOffset lastWriteUtc))
            {
                if (File.Exists(pending.FullPath))
                    RecordFailure(generation, relativePath, new IOException("文件暂不可读，将稍后重试。"));
                else
                    RemovePendingByRelativePath(generation, relativePath);
                return;
            }

            if (observedLength != length || observedLastWriteUtc != lastWriteUtc)
            {
                ObserveChangedFile(generation, relativePath, length, lastWriteUtc);
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < notBeforeUtc || now < nextAttemptUtc) return;
            if (_stateStore.TryGet(
                    generation.ConfigurationFingerprint, relativePath, out AutomaticBackupState? state) &&
                state!.Length == length && state.LastWriteUtc == lastWriteUtc)
            {
                RemovePendingIfUnchanged(generation, relativePath, length, lastWriteUtc);
                return;
            }

            snapshotAttemptDirectory = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshotAttemptDirectory);
            snapshotPath = Path.Combine(snapshotAttemptDirectory, Path.GetFileName(pending.FullPath));
            string? sha256 = await CopyStableSnapshotAsync(
                generation, relativePath, pending, length, lastWriteUtc, snapshotPath, ct).ConfigureAwait(false);
            if (sha256 is null) return;

            if (_stateStore.TryGet(
                    generation.ConfigurationFingerprint, relativePath, out state) &&
                state!.Length == length && state.LastWriteUtc == lastWriteUtc &&
                string.Equals(state.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                RemovePendingIfUnchanged(generation, relativePath, length, lastWriteUtc);
                return;
            }

            if (state is not null && string.Equals(state.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                var refreshed = new AutomaticBackupState(length, lastWriteUtc, sha256, state.LastSuccessUtc);
                if (!_stateStore.Upsert(generation.ConfigurationFingerprint, relativePath, refreshed))
                    throw new IOException("无法保存自动备份状态。");
                RemovePendingIfUnchanged(generation, relativePath, length, lastWriteUtc);
                return;
            }

            await _uploadAsync(
                generation.Target,
                AutomaticBackupFileFilter.GetRelativeDirectory(relativePath),
                snapshotPath,
                ct).ConfigureAwait(false);
            DateTimeOffset successUtc = DateTimeOffset.UtcNow;
            var uploaded = new AutomaticBackupState(length, lastWriteUtc, sha256, successUtc);
            if (!_stateStore.Upsert(generation.ConfigurationFingerprint, relativePath, uploaded))
                throw new IOException("无法保存自动备份状态。");

            RemovePendingIfUnchanged(generation, relativePath, length, lastWriteUtc);
            LastSuccessUtc = successUtc;
            LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            RemovePendingByRelativePath(generation, relativePath);
        }
        catch (DirectoryNotFoundException)
        {
            RemovePendingByRelativePath(generation, relativePath);
        }
        catch (Exception ex)
        {
            RecordFailure(generation, relativePath, ex);
        }
        finally
        {
            if (snapshotPath is not null)
            {
                try
                {
                    File.Delete(snapshotPath);
                }
                catch
                {
                    // Private leftovers have unique names and are never treated as source files.
                }
            }

            if (snapshotAttemptDirectory is not null)
            {
                try
                {
                    Directory.Delete(snapshotAttemptDirectory, recursive: false);
                }
                catch
                {
                    // The directory may still be held by a provider finishing its synchronous cleanup.
                }
            }
        }
    }

    private async Task<string?> CopyStableSnapshotAsync(
        Generation generation,
        string relativePath,
        PendingFile pending,
        long expectedLength,
        DateTimeOffset expectedLastWriteUtc,
        string snapshotPath,
        CancellationToken ct)
    {
        if (!TryReadMetadata(pending.FullPath, out long beforeLength, out DateTimeOffset beforeWrite) ||
            beforeLength != expectedLength || beforeWrite != expectedLastWriteUtc)
        {
            ObserveChangedFileOrRemove(generation, relativePath, pending.FullPath);
            return null;
        }

        await using (var source = new FileStream(
                         pending.FullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         snapshotPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!TryReadMetadata(pending.FullPath, out beforeLength, out beforeWrite) ||
                beforeLength != expectedLength || beforeWrite != expectedLastWriteUtc)
            {
                ObserveChangedFileOrRemove(generation, relativePath, pending.FullPath);
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            if (!TryReadMetadata(pending.FullPath, out long afterLength, out DateTimeOffset afterWrite) ||
                afterLength != expectedLength || afterWrite != expectedLastWriteUtc)
            {
                ObserveChangedFileOrRemove(generation, relativePath, pending.FullPath);
                return null;
            }

            lock (generation.Sync)
            {
                if (!generation.Pending.TryGetValue(relativePath, out PendingFile? current) ||
                    current.Length != expectedLength || current.LastWriteUtc != expectedLastWriteUtc)
                    return null;
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    private void ObserveChangedFileOrRemove(Generation generation, string relativePath, string fullPath)
    {
        if (TryReadMetadata(fullPath, out long length, out DateTimeOffset lastWriteUtc))
            ObserveChangedFile(generation, relativePath, length, lastWriteUtc);
        else if (File.Exists(fullPath))
            RecordFailure(generation, relativePath, new IOException("文件暂不可读，将稍后重试。"));
        else
            RemovePendingByRelativePath(generation, relativePath);
    }

    private void ObserveChangedFile(
        Generation generation,
        string relativePath,
        long length,
        DateTimeOffset lastWriteUtc)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (generation.Sync)
        {
            if (!generation.Pending.TryGetValue(relativePath, out PendingFile? pending)) return;
            pending.Length = length;
            pending.LastWriteUtc = lastWriteUtc;
            pending.NotBeforeUtc = now + generation.StableWindow;
            pending.NextAttemptUtc = pending.NotBeforeUtc;
            pending.Failures = 0;
        }
    }

    private void RecordFailure(Generation generation, string relativePath, Exception exception)
    {
        lock (generation.Sync)
        {
            if (!generation.Pending.TryGetValue(relativePath, out PendingFile? pending)) return;
            pending.Failures++;
            int exponent = Math.Min(pending.Failures - 1, 16);
            double milliseconds = Math.Min(
                MaximumRetryDelay.TotalMilliseconds,
                _initialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent));
            pending.NextAttemptUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        }

        LastError = exception.Message;
    }

    private static bool TryReadMetadata(
        string fullPath,
        out long length,
        out DateTimeOffset lastWriteUtc)
    {
        length = 0;
        lastWriteUtc = default;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;
            length = info.Length;
            lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private bool IsSnapshotPath(string fullPath)
    {
        try
        {
            return AutomaticBackupFileFilter.TryGetRelativePath(_snapshotDirectory, fullPath, out _);
        }
        catch
        {
            return false;
        }
    }

    private async Task StopCurrentGenerationAsync()
    {
        Generation? generation = _generation;
        _generation = null;
        IsRunning = false;
        if (generation is null)
        {
            PendingCount = 0;
            return;
        }

        generation.Deactivate();
        try
        {
            generation.Cancellation.Cancel();
            if (generation.Worker is not null)
                await generation.Worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation interrupts the worker's wait or upload.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            generation.Dispose();
            PendingCount = 0;
        }
    }

    private sealed class Generation
    {
        private int _active = 1;
        private DateTimeOffset _nextFullScanUtc;

        public Generation(
            string rootDirectory,
            string configurationFingerprint,
            NetdiskUploadTarget target,
            TimeSpan stableWindow,
            TimeSpan rescanInterval)
        {
            RootDirectory = rootDirectory;
            ConfigurationFingerprint = configurationFingerprint;
            Target = target;
            StableWindow = stableWindow;
            RescanInterval = rescanInterval;
            NextFullScanUtc = DateTimeOffset.UtcNow;
        }

        public string RootDirectory { get; }
        public string ConfigurationFingerprint { get; }
        public NetdiskUploadTarget Target { get; }
        public TimeSpan StableWindow { get; }
        public TimeSpan RescanInterval { get; }
        public object Sync { get; } = new();
        public Dictionary<string, PendingFile> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0, 1);
        public DateTimeOffset NextFullScanUtc
        {
            get { lock (Sync) return _nextFullScanUtc; }
            set { lock (Sync) _nextFullScanUtc = value; }
        }
        public FileSystemWatcher? Watcher { get; set; }
        public Task? Worker { get; set; }
        public bool Active => Volatile.Read(ref _active) != 0;

        public void Deactivate()
        {
            Interlocked.Exchange(ref _active, 0);
            Watcher?.Dispose();
        }

        public void Dispose()
        {
            Deactivate();
            Cancellation.Dispose();
            Signal.Dispose();
        }
    }

    private sealed class PendingFile
    {
        public PendingFile(string fullPath) => FullPath = fullPath;

        public string FullPath { get; }
        public long Length { get; set; } = -1;
        public DateTimeOffset LastWriteUtc { get; set; }
        public DateTimeOffset NotBeforeUtc { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
        public int Failures { get; set; }
    }
}
