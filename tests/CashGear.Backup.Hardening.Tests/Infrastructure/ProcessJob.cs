using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// A kill-on-close Job Object around one agent process. Everything the agent starts (the kopia server, kopia CLI
/// checks) lands in it too, so the job lists the agent's whole process tree, reports its peak memory, and nothing
/// outlives the test process. The engine's own kill-on-close job around kopia nests inside this one.
/// </summary>
internal sealed partial class ProcessJob : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int ErrorMoreData = 234;

    private readonly SafeJobHandle _handle;

    public ProcessJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var info = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Ids of the processes in the job that have not exited.</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        for (var capacity = 64; ; capacity *= 4)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORDs, then ULONG_PTR ids.
            var size = (2 * sizeof(uint)) + (capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)size, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorMoreData)
                    {
                        continue;
                    }

                    throw new Win32Exception(error, "QueryInformationJobObject failed.");
                }

                var assigned = Marshal.ReadInt32(buffer);
                var listed = Marshal.ReadInt32(buffer, sizeof(uint));
                if (listed < assigned)
                {
                    continue;
                }

                var ids = new List<int>(listed);
                for (var i = 0; i < listed; i++)
                {
                    ids.Add((int)Marshal.ReadIntPtr(buffer, (2 * sizeof(uint)) + (i * IntPtr.Size)));
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Peak committed (private) memory since the job was created: of the largest single process, and of all its
    /// processes together.
    /// </summary>
    public (long PeakProcess, long PeakJob) PeakCommit()
    {
        var info = default(ExtendedLimitInformation);
        var size = (uint)Marshal.SizeOf<ExtendedLimitInformation>();
        if (!QueryExtendedInformation(_handle, JobObjectExtendedLimitInformation, ref info, size, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
        }

        return ((long)info.PeakProcessMemoryUsed, (long)info.PeakJobMemoryUsed);
    }

    /// <summary>Closes the job, which terminates every process still in it.</summary>
    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, IntPtr info, uint length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryExtendedInformation(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed partial class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
