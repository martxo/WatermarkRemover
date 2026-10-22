using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WatermarkRemover.Index.Ipc;

/// <summary>
/// Creates the host's pipe with an explicit security descriptor. This is the one place where the
/// privileged host and the un-elevated app meet, and both halves of the descriptor matter:
///   D: … — only the signed-in user (plus SYSTEM/Administrators) may open the pipe, matching
///          PipeOptions.CurrentUserOnly;
///   S: … — the mandatory integrity label must be *medium*, otherwise the high-integrity host's
///          pipe rejects every write from the medium-integrity app (ERROR_ACCESS_DENIED).
/// The label cannot be added after creation: the handle returned by NamedPipeServerStream does not
/// carry WRITE_DAC. So the pipe is created here and wrapped in a managed stream.
/// </summary>
internal static class PrivilegedPipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SddlRevision1 = 1;
    private const int BufferSize = 64 * 1024;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static NamedPipeServerStream CreateServerStream(string pipeName, int maxInstances)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? userSid = identity.User?.Value;
        if (string.IsNullOrEmpty(userSid))
            throw new InvalidOperationException("无法确定当前用户 SID，索引管道不可用。");

        // GA (generic all) for the user, SYSTEM and Administrators; the app runs as this user.
        string sddl =
            $"D:(A;;GA;;;{userSid})(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, SddlRevision1, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };

            IntPtr handle = CreateNamedPipeW(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeRejectRemoteClients,
                (uint)maxInstances,
                BufferSize,
                BufferSize,
                0,
                ref attributes);

            if (handle == InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // The stream owns the handle; isAsync must match FILE_FLAG_OVERLAPPED.
            return new NamedPipeServerStream(
                PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(handle, true));
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorLength,
        out IntPtr securityDescriptor,
        out int securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        int outBufferSize,
        int inBufferSize,
        int defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
