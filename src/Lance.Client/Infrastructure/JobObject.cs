using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lance.Client.Infrastructure;

// A Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: every process assigned
// to it dies when the job handle closes — i.e. when this daemon exits for any reason
// (clean exit, crash, console close). On non-Windows this is a no-op; the daemon relies
// on tree-kill for clean exits and the agent's probe-watch for hard death.
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly IntPtr _handle;

    public JobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            _handle = IntPtr.Zero;
            return;
        }

        _handle = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool IsActive
    {
        get { return _handle != IntPtr.Zero; }
    }

    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        AssignProcessToJobObject(_handle, process.Handle);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);   // KILL_ON_JOB_CLOSE fires here for any surviving children
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
