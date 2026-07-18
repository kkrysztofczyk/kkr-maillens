using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KKR.MailLens;

sealed class WorkerProcessLimit : IDisposable
{
    const uint JobObjectLimitJobMemory = 0x00000200;
    const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    const uint JobObjectLimitKillOnJobClose = 0x00002000;
    readonly SafeFileHandle job;

    WorkerProcessLimit(SafeFileHandle job) => this.job = job;

    public static WorkerProcessLimit? TryAttach(Process process, long memoryBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (memoryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBytes));
        error = null;
        if (!OperatingSystem.IsWindows()) return null;

        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            job.Dispose();
            return null;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitJobMemory | JobObjectLimitDieOnUnhandledException
                    | JobObjectLimitKillOnJobClose,
            },
            JobMemoryLimit = (UIntPtr)(ulong)memoryBytes,
        };
        int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, 9, buffer, (uint)length)
                || !AssignProcessToJobObject(job, process.Handle))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                job.Dispose();
                return null;
            }
            return new WorkerProcessLimit(job);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() => job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
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
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
