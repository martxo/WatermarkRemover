using WatermarkRemover.Index.Native;
using WatermarkRemover.Index.Ntfs;

namespace WatermarkRemover.Index.Indexing;

/// <summary>
/// Owns one volume's lifecycle, mirroring Everything's per-volume NTFS monitor:
/// open the volume (admin), query the USN journal, restore a snapshot or walk the MFT,
/// then apply journal records once a second.
/// </summary>
public sealed class VolumeIndexer
{
    private const int PollIntervalMs = 1000;
    private const int SnapshotIntervalMs = 60_000;

    private readonly string _rootPath;
    private readonly string _snapshotPath;
    private readonly object _sync = new();

    private volatile FileIndexStore _store;
    private long _nextUsn;
    private ulong _journalId;
    private volatile bool _ready;
    private volatile bool _scanning;
    private volatile string? _error;

    public VolumeIndexer(string rootPath, string stateDirectory)
    {
        _rootPath = rootPath;
        _snapshotPath = IndexSnapshot.DefaultPath(stateDirectory, rootPath);
        _store = new FileIndexStore(rootPath);
    }

    public string RootPath => _rootPath;

    public FileIndexStore Store => _store;

    public bool IsReady => _ready;

    public bool IsScanning => _scanning;

    public string? Error => _error;

    public void Start(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using NtfsVolume volume = NtfsVolume.Open(_rootPath);
            UsnJournalInfo journal = volume.EnsureJournal();
            _journalId = journal.Id;

            // Restoring a snapshot is fast but still leaves the store without its newest entries,
            // so report "building" until the volume is actually query-complete.
            _scanning = true;
            if (!TryRestore(volume, journal, cancellationToken))
                FullScan(volume, journal, cancellationToken);

            _ready = true;
            _scanning = false;
            _error = null;

            await MonitorAsync(volume, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (UnauthorizedAccessException ex)
        {
            _error = ex.Message;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private bool TryRestore(NtfsVolume volume, UsnJournalInfo journal, CancellationToken cancellationToken)
    {
        if (!IndexSnapshot.TryLoad(_snapshotPath, _rootPath, out SnapshotData? snapshot) || snapshot is null)
            return false;

        // Same checks as Everything's "check journal ids": id must match and the saved USN must
        // still be reachable. Otherwise the snapshot describes a volume that no longer exists.
        if (snapshot.JournalId != journal.Id) return false;
        if (snapshot.NextUsn < journal.FirstUsn || snapshot.NextUsn > journal.NextUsn) return false;

        var restored = new FileIndexStore(_rootPath);
        restored.Load(snapshot.Entries);

        _store = restored;
        _nextUsn = snapshot.NextUsn;

        try
        {
            ApplyUntil(volume, journal.NextUsn, cancellationToken);
            return true;
        }
        catch (JournalResetException)
        {
            _store = new FileIndexStore(_rootPath);
            return false;
        }
    }

    private void FullScan(NtfsVolume volume, UsnJournalInfo journal, CancellationToken cancellationToken)
    {
        _scanning = true;
        try
        {
            var fresh = new FileIndexStore(_rootPath);
            long highUsn = journal.NextUsn;

            volume.EnumerateAll(record =>
            {
                if (FileIndexStore.IsRoot(record.FileId)) return;
                if (record.Name.Length == 0) return;
                fresh.Upsert(new IndexEntry(
                    record.FileId, record.ParentFileId, record.Attributes, record.Name));
            }, highUsn, cancellationToken);

            _store = fresh;
            _nextUsn = highUsn;
            SaveSnapshot();
        }
        finally
        {
            _scanning = false;
        }
    }

    private void ApplyUntil(NtfsVolume volume, long targetUsn, CancellationToken cancellationToken)
    {
        long current = _nextUsn;
        int guard = 0;

        while (current < targetUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UsnReadResult result = volume.ReadJournal(current, _journalId);
            if (result.JournalReset) throw new JournalResetException();

            foreach (UsnRecord record in result.Records) ApplyRecord(record);

            if (result.NextUsn <= current) break;
            current = result.NextUsn;

            if (++guard > 4096) break;
        }

        _nextUsn = current;
    }

    private void ApplyRecord(UsnRecord record)
    {
        if (FileIndexStore.IsRoot(record.FileId) || record.Name.Length == 0) return;

        // Delete wins when a batch contains create+delete for a short-lived file.
        if ((record.Reason & UsnRecordParser.ReasonFileDelete) != 0)
        {
            _store.Remove(record.FileId);
            return;
        }

        if ((record.Reason & (UsnRecordParser.ReasonFileCreate | UsnRecordParser.ReasonRenameNewName)) != 0)
        {
            _store.Upsert(new IndexEntry(
                record.FileId, record.ParentFileId, record.Attributes, record.Name));
        }
    }

    private async Task MonitorAsync(NtfsVolume volume, CancellationToken cancellationToken)
    {
        long lastSnapshot = Environment.TickCount64;
        bool dirty = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);

                UsnJournalInfo journal = volume.QueryJournal();

                if (journal.Id != _journalId || (_nextUsn < journal.FirstUsn && journal.NextUsn > _nextUsn))
                {
                    // The snapshot must carry the journal id it was built from, so switch first.
                    _journalId = journal.Id;
                    FullScan(volume, journal, cancellationToken);
                    dirty = true;
                    continue;
                }

                if (journal.NextUsn > _nextUsn)
                {
                    ApplyUntil(volume, journal.NextUsn, cancellationToken);
                    dirty = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }

            if (dirty && Environment.TickCount64 - lastSnapshot >= SnapshotIntervalMs)
            {
                TrySaveSnapshot();
                dirty = false;
                lastSnapshot = Environment.TickCount64;
            }
        }

        if (dirty) TrySaveSnapshot();
    }

    private void SaveSnapshot()
    {
        lock (_sync)
        {
            FileIndexStore store = _store;
            IndexSnapshot.Save(
                _snapshotPath, _rootPath, _journalId, _nextUsn, store.Entries, store.FolderCount);
        }
    }

    private void TrySaveSnapshot()
    {
        try { SaveSnapshot(); }
        catch (Exception ex) { _error = "索引快照写入失败：" + ex.Message; }
    }

    private sealed class JournalResetException : Exception
    {
    }
}
