using System.Text;

namespace WatermarkRemover.Index.Indexing;

public sealed record SnapshotData(
    ulong JournalId,
    long NextUsn,
    List<IndexEntry> Entries,
    int FolderCount);

/// <summary>
/// Binary snapshot of one volume's index — the analogue of Everything.db. It is only trusted
/// when the journal id still matches AND the saved USN is still inside the live journal; otherwise
/// the caller rebuilds from the MFT. Saving goes through .tmp + move so a crash cannot leave a
/// half-written index behind.
/// </summary>
public static class IndexSnapshot
{
    private const uint Magic = 0x58444957; // "WIDX"
    private const int FormatVersion = 1;

    public static void Save(
        string path,
        string rootPath,
        ulong journalId,
        long nextUsn,
        IEnumerable<IndexEntry> entries,
        int folderCount)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(rootPath);
            writer.Write(journalId);
            writer.Write(nextUsn);

            // The count has to be known up front; materialising the entry list is the price of a
            // compact format. Snapshots run at most once a minute and off the query path.
            var buffer = entries as ICollection<IndexEntry> ?? entries.ToList();
            writer.Write(buffer.Count);
            foreach (IndexEntry entry in buffer)
            {
                writer.Write(entry.FileId);
                writer.Write(entry.ParentFileId);
                writer.Write(entry.Attributes);
                writer.Write(entry.Name);
            }

            writer.Write(folderCount);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static bool TryLoad(string path, string rootPath, out SnapshotData? snapshot)
    {
        snapshot = null;
        if (!File.Exists(path)) return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadUInt32() != Magic) return false;
            if (reader.ReadInt32() != FormatVersion) return false;
            if (!string.Equals(reader.ReadString(), rootPath, StringComparison.OrdinalIgnoreCase)) return false;

            ulong journalId = reader.ReadUInt64();
            long nextUsn = reader.ReadInt64();
            int count = reader.ReadInt32();
            if (count < 0) return false;

            var entries = new List<IndexEntry>(count);
            for (int i = 0; i < count; i++)
            {
                ulong fileId = reader.ReadUInt64();
                ulong parentId = reader.ReadUInt64();
                uint attributes = reader.ReadUInt32();
                string name = reader.ReadString();
                entries.Add(new IndexEntry(fileId, parentId, attributes, name));
            }

            int folderCount = 0;
            try { folderCount = reader.ReadInt32(); }
            catch (EndOfStreamException) { folderCount = entries.Count(e => e.IsFolder); }

            snapshot = new SnapshotData(journalId, nextUsn, entries, folderCount);
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            return false;
        }
    }

    public static string DefaultPath(string stateDirectory, string rootPath)
    {
        string volume = rootPath.Length >= 2 && rootPath[1] == ':'
            ? rootPath[..1].ToUpperInvariant()
            : "volume";
        return Path.Combine(stateDirectory, $"index-{volume}.bin");
    }
}
