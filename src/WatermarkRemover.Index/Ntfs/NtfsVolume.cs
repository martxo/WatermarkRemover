using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WatermarkRemover.Index.Native;

namespace WatermarkRemover.Index.Ntfs;

public readonly record struct UsnJournalInfo(ulong Id, long FirstUsn, long NextUsn);

public readonly record struct UsnReadResult(IReadOnlyList<UsnRecord> Records, long NextUsn, bool JournalReset);

/// <summary>An FSCTL failed; <see cref="NativeErrorCode"/> carries the raw Win32 code.</summary>
public sealed class NtfsException : IOException
{
    public NtfsException(string message, int nativeErrorCode)
        : base($"{message} (Win32 {nativeErrorCode}: {new Win32Exception(nativeErrorCode).Message})")
    {
        NativeErrorCode = nativeErrorCode;
    }

    public int NativeErrorCode { get; }
}

/// <summary>
/// A volume opened for reading, plus the two journal operations the index is built from:
/// FSCTL_ENUM_USN_DATA (walk every MFT record once) and FSCTL_READ_USN_JOURNAL (apply changes).
///
/// Opening requires GENERIC_READ on the volume, which Windows only grants to an elevated token.
/// That is the same hard boundary Everything hits; its service exists purely to hold this handle.
/// </summary>
public sealed class NtfsVolume : IDisposable
{
    private const int BufferSize = 1024 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly IntPtr _inBuffer;
    private readonly IntPtr _outBuffer;
    private readonly byte[] _scratch = new byte[BufferSize];
    private readonly List<UsnRecord> _batch = new(4096);

    private NtfsVolume(SafeFileHandle handle, string rootPath, string devicePath)
    {
        _handle = handle;
        RootPath = rootPath;
        DevicePath = devicePath;
        _inBuffer = Marshal.AllocHGlobal(64);
        _outBuffer = Marshal.AllocHGlobal(BufferSize);
    }

    /// <summary>e.g. <c>C:\</c>.</summary>
    public string RootPath { get; }

    /// <summary>e.g. <c>\\.\C:</c>.</summary>
    public string DevicePath { get; }

    /// <summary>Opens the device behind a drive root. Throws <see cref="UnauthorizedAccessException"/>
    /// when the process is not elevated.</summary>
    public static NtfsVolume Open(string rootPath)
    {
        string devicePath = ToDevicePath(rootPath);
        SafeFileHandle handle = NtfsInterop.CreateFileW(
            devicePath,
            NtfsInterop.GenericRead,
            NtfsInterop.FileShareRead | NtfsInterop.FileShareWrite,
            IntPtr.Zero,
            NtfsInterop.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == NtfsInterop.ErrorAccessDenied)
                throw new UnauthorizedAccessException(
                    $"读取 {rootPath} 的 NTFS 主文件表需要管理员权限（Win32 {error}）。");
            throw new NtfsException($"打开卷 {devicePath} 失败", error);
        }

        return new NtfsVolume(handle, rootPath, devicePath);
    }

    public static string ToDevicePath(string rootPath)
    {
        string trimmed = rootPath.TrimEnd('\\', '/');
        return trimmed.Length >= 2 && trimmed[1] == ':' ? @"\\.\" + trimmed : trimmed;
    }

    public UsnJournalInfo QueryJournal()
    {
        if (!NtfsInterop.DeviceIoControl(
                _handle, NtfsInterop.FsctlQueryUsnJournal, IntPtr.Zero, 0,
                _outBuffer, 64, out int returned, IntPtr.Zero))
        {
            throw new NtfsException($"查询 {RootPath} 的 USN 日志失败", Marshal.GetLastWin32Error());
        }

        if (returned < 24) throw new NtfsException($"USN 日志信息不完整（{returned} 字节）", 0);

        return new UsnJournalInfo(
            (ulong)Marshal.ReadInt64(_outBuffer, 0),
            Marshal.ReadInt64(_outBuffer, 8),
            Marshal.ReadInt64(_outBuffer, 16));
    }

    /// <summary>
    /// Makes sure the volume has a USN journal. Windows keeps one active by default on NTFS;
    /// a freshly formatted or explicitly disabled volume answers ERROR_JOURNAL_NOT_ACTIVE and
    /// needs this one-time creation (Everything exposes the same operation as -create-usn-journal).
    /// </summary>
    public UsnJournalInfo EnsureJournal(
        long maximumSizeBytes = 32L * 1024 * 1024,
        long allocationDeltaBytes = 4L * 1024 * 1024)
    {
        try
        {
            return QueryJournal();
        }
        catch (NtfsException ex) when (ex.NativeErrorCode == NtfsInterop.ErrorJournalNotActive)
        {
            Marshal.WriteInt64(_inBuffer, 0, maximumSizeBytes);
            Marshal.WriteInt64(_inBuffer, 8, allocationDeltaBytes);

            if (!NtfsInterop.DeviceIoControl(
                    _handle, NtfsInterop.FsctlCreateUsnJournal,
                    _inBuffer, 16, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new NtfsException($"为 {RootPath} 创建 USN 日志失败", Marshal.GetLastWin32Error());
            }

            return QueryJournal();
        }
    }

    /// <summary>
    /// Walks every record of the MFT through FSCTL_ENUM_USN_DATA. This is the bulk-index path:
    /// one sequential kernel-side sweep instead of a directory-by-directory traversal.
    /// </summary>
    public void EnumerateAll(Action<UsnRecord> sink, long highUsn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        ulong start = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Marshal.WriteInt64(_inBuffer, 0, (long)start);
            Marshal.WriteInt64(_inBuffer, 8, 0);          // LowUsn
            Marshal.WriteInt64(_inBuffer, 16, highUsn);   // HighUsn

            if (!NtfsInterop.DeviceIoControl(
                    _handle, NtfsInterop.FsctlEnumUsnData,
                    _inBuffer, 24, _outBuffer, BufferSize, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NtfsInterop.ErrorHandleEof) break;
                throw new NtfsException($"枚举 {RootPath} 的 MFT 失败", error);
            }

            if (returned < 8) break;

            Marshal.Copy(_outBuffer, _scratch, 0, returned);
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(_scratch);

            _batch.Clear();
            UsnRecordParser.ParseAll(_scratch.AsSpan(8, returned - 8), _batch);
            foreach (UsnRecord record in _batch) sink(record);

            if (next == 0 || next == start) break;
            start = next;
        }
    }

    /// <summary>
    /// Reads the records written after <paramref name="startUsn"/>. A journal that no longer
    /// contains that USN (id changed, or the range was purged) is reported as a reset, which the
    /// caller answers with a full rebuild — exactly what Everything's "check journal ids" does.
    /// </summary>
    public UsnReadResult ReadJournal(long startUsn, ulong journalId)
    {
        Marshal.WriteInt64(_inBuffer, 0, startUsn);
        Marshal.WriteInt32(_inBuffer, 8, unchecked((int)0xFFFFFFFF)); // ReasonMask: everything
        Marshal.WriteInt32(_inBuffer, 12, 0);                        // ReturnOnlyOnClose
        Marshal.WriteInt64(_inBuffer, 16, 0);                        // Timeout
        Marshal.WriteInt64(_inBuffer, 24, 0);                        // BytesToWaitFor
        Marshal.WriteInt64(_inBuffer, 32, (long)journalId);

        if (!NtfsInterop.DeviceIoControl(
                _handle, NtfsInterop.FsctlReadUsnJournal,
                _inBuffer, 40, _outBuffer, BufferSize, out int returned, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is NtfsInterop.ErrorJournalEntryDeleted or NtfsInterop.ErrorJournalNotActive
                or NtfsInterop.ErrorJournalDeleteInProgress)
            {
                return new UsnReadResult(Array.Empty<UsnRecord>(), startUsn, true);
            }

            throw new NtfsException($"读取 {RootPath} 的 USN 日志失败", error);
        }

        if (returned < 8) return new UsnReadResult(Array.Empty<UsnRecord>(), startUsn, false);

        Marshal.Copy(_outBuffer, _scratch, 0, returned);
        long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(_scratch);

        var records = new List<UsnRecord>();
        UsnRecordParser.ParseAll(_scratch.AsSpan(8, returned - 8), records);
        return new UsnReadResult(records, nextUsn, false);
    }

    public void Dispose()
    {
        _handle.Dispose();
        Marshal.FreeHGlobal(_inBuffer);
        Marshal.FreeHGlobal(_outBuffer);
    }
}
