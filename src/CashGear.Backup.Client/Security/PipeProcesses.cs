using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CashGear.Backup.Client.Security;

/// <summary>Which processes are at the ends of a named pipe, and who they run as.</summary>
[SupportedOSPlatform("windows")]
internal static partial class PipeProcesses
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    public static int ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var id) ? checked((int)id) : throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetNamedPipeServerProcessId failed.");

    public static int ClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var id) ? checked((int)id) : throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetNamedPipeClientProcessId failed.");

    /// <summary>The process id of a running service; null when it is not installed or not running.</summary>
    public static int? ServiceProcessId(string serviceName)
    {
        using var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            return null;
        }

        using var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
        if (service.IsInvalid)
        {
            return null;
        }

        var status = default(ServiceStatusProcess);
        return QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) && status.ProcessId != 0
            ? checked((int)status.ProcessId)
            : null;
    }

    /// <summary>The account a process runs as; null when Windows does not let us look.</summary>
    public static unsafe SecurityIdentifier? ProcessUser(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            GetTokenInformation(token, TokenUserClass, null, 0, out var length);
            if (length <= 0)
            {
                return null;
            }

            var buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                // TOKEN_USER starts with SID_AND_ATTRIBUTES, which starts with the SID pointer.
                return GetTokenInformation(token, TokenUserClass, p, length, out _) ? new SecurityIdentifier(*(IntPtr*)p) : null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, ref ServiceStatusProcess status, uint size, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeKernelHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeKernelHandle process, uint desiredAccess, out SafeKernelHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(SafeKernelHandle token, int informationClass, byte* information, int length, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
