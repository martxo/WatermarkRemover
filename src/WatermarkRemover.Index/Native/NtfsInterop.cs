using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WatermarkRemover.Index.Native;

/// <summary>
/// Raw Win32 surface used by the NTFS reader. Only the handful of calls Everything's own
/// ntfs.c needs: open the volume, then drive FSCTLs for the USN journal and MFT enumeration.
/// </summary>
internal static class NtfsInterop
{
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    public const uint FsctlEnumUsnData = 0x000900B3;
    public const uint FsctlReadUsnJournal = 0x000900BB;
    public const uint FsctlQueryUsnJournal = 0x000900F4;
    public const uint FsctlCreateUsnJournal = 0x000900DF;

    public const int ErrorAccessDenied = 5;
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
