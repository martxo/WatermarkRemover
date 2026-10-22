using System.Runtime.InteropServices;
using System.Text;

namespace WatermarkRemover.Core.Features.Search;

/// <summary>
/// Queries a RUNNING Everything client through the official SDK IPC wrapper
/// (Everything64.dll, shipped beside the exe). The DLL is a thin IPC client, not a search engine:
/// without Everything running every call yields nothing and <see cref="IsAvailable"/> stays false.
///
/// The SDK keeps one global query state per process, so every call is serialised behind a lock.
/// Queries are blocking by nature; they are pushed onto the thread pool so the UI never waits.
/// </summary>
public sealed class EverythingSearchProvider : IFileSearchProvider
{
    private const uint RequestFileName = 0x00000001;
    private const uint RequestPath = 0x00000002;
    private const uint RequestFullPath = 0x00000004;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    private const uint SortDateModifiedDescending = 14;
    private const uint BufferChars = 32768;

    private readonly object _gate = new();

    public string DisplayName => "Everything";

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return Everything_GetMajorVersion() > 0;
                }
                catch (Exception ex) when (IsMissingNative(ex))
                {
                    return false;
                }
            }
        }
    }

    public Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Task.FromResult<IReadOnlyList<FileSearchResult>>(Array.Empty<FileSearchResult>());

        string trimmed = query.Trim();
        return Task.Run(() => Search(trimmed, maxResults, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<FileSearchResult> Search(string query, int maxResults, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                if (Everything_GetMajorVersion() == 0) return Array.Empty<FileSearchResult>();

                Everything_SetSearchW(query);
                Everything_SetMatchPath(false);
                Everything_SetMatchCase(false);
                Everything_SetMatchWholeWord(false);
                Everything_SetRegex(false);
                Everything_SetRequestFlags(
                    RequestFileName | RequestPath | RequestFullPath | RequestSize | RequestDateModified);
                Everything_SetSort(SortDateModifiedDescending);
                Everything_SetMax((uint)maxResults);

                cancellationToken.ThrowIfCancellationRequested();
                if (!Everything_QueryW(true)) return Array.Empty<FileSearchResult>();
                cancellationToken.ThrowIfCancellationRequested();

                uint count = Everything_GetNumResults();
                var results = new List<FileSearchResult>((int)Math.Min(count, (uint)maxResults));
                var buffer = new StringBuilder((int)BufferChars);

                for (uint i = 0; i < count; i++)
                {
                    buffer.Clear();
                    Everything_GetResultFullPathNameW(i, buffer, BufferChars);
                    string fullPath = buffer.ToString();
                    if (fullPath.Length == 0) continue;

                    long size = 0;
                    Everything_GetResultSize(i, out size);

                    DateTime? modified = null;
                    if (Everything_GetResultDateModified(i, out long fileTime) && fileTime > 0)
                    {
                        try { modified = DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
                        catch (ArgumentOutOfRangeException) { }
                    }

                    results.Add(new FileSearchResult(fullPath, Everything_IsFolderResult(i), size, modified));
                }

                return results;
            }
            catch (Exception ex) when (IsMissingNative(ex))
            {
                return Array.Empty<FileSearchResult>();
            }
        }
    }

    private static bool IsMissingNative(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint Everything_GetMajorVersion();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetSearchW(string lpString);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetMatchPath(bool enable);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetMatchCase(bool enable);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetMatchWholeWord(bool enable);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetRegex(bool enable);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetRequestFlags(uint flags);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetSort(uint sort);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetMax(uint max);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Everything_QueryW(bool wait);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_GetResultFullPathNameW(uint index, StringBuilder buffer, uint maxCount);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Everything_GetResultSize(uint index, out long size);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Everything_GetResultDateModified(uint index, out long fileTime);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Everything_IsFolderResult(uint index);
}
