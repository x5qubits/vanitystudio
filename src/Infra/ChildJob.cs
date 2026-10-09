using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VanityStudio.Infra;

/// <summary>
/// Ties every child process the agent starts (php -S, the persistent shell, tool commands, the headless browser) to
/// the agent's own lifetime through one Windows job object with KILL_ON_JOB_CLOSE. When the host process ends for
/// ANY reason - a normal exit, a crash, the debugger's Stop, Task Manager - the kernel closes the job and kills what
/// is left in it. Before this, a Slave interrupted mid-run left php.exe serving forever (2026-09-21).
/// No-op off Windows, and never fatal: a process that cannot be assigned (already in a non-nestable job on an old
/// Windows) simply keeps the old behaviour.
/// </summary>
public static class ChildJob
{
    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _tried;

    /// <summary>Put <paramref name="process"/> in the agent's job. Call right after Start().</summary>
    public static void Own(Process? process)
    {
        if (process is null || !OperatingSystem.IsWindows()) return;
        try
        {
            var job = Job();
            if (job == IntPtr.Zero) return;
            AssignProcessToJobObject(job, process.Handle);
        }
        catch { /* best effort - see the class note */ }
    }

    private static IntPtr Job()
    {
        lock (Gate)
        {
            if (_tried) return _job;
            _tried = true;
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size)) { CloseHandle(job); return IntPtr.Zero; }
                }
                finally { Marshal.FreeHGlobal(ptr); }
                _job = job;   // deliberately never closed: closing it is what kills the children, and that is the host's death
                return _job;
            }
            catch { return IntPtr.Zero; }
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
        public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
