using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NekoPlayer.Infrastructure.Online;

/// <summary>The gateway and every descendant terminate if this application's job handle is closed.</summary>
internal sealed class WindowsGatewayJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    private WindowsGatewayJob(SafeFileHandle handle) => _handle = handle;

    public static WindowsGatewayJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var job = new WindowsGatewayJob(handle);
        try
        {
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x00002000 }
            };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    public void Dispose() => _handle.Dispose();

    public async Task TerminateAndWaitAsync()
    {
        if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            if (!QueryInformationJobObject(_handle, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (accounting.ActiveProcesses == 0) return;
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                throw new GatewayException(GatewayFailureKind.Timeout, "gateway_shutdown_timeout", "在线音乐进程未能及时退出");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal static async Task WaitForWorkingDirectoryReleaseAsync(string directory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory)) return;
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            // Request DELETE access without deleting anything. A cwd handle lacking FILE_SHARE_DELETE
            // still causes ERROR_SHARING_VIOLATION even after the owning process has become signalled.
            using var probe = CreateFile(directory, 0x00010000, 0x00000007, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!probe.IsInvalid) return;
            var error = Marshal.GetLastWin32Error();
            // Read-only installation ACLs need not grant DELETE access. Missing directories are already released.
            if (error is 2 or 3 or 5) return;
            if (error != 32) throw new Win32Exception(error);
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                throw new GatewayException(GatewayFailureKind.Timeout, "gateway_handles_timeout", "在线音乐进程的目录句柄未能及时释放");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
        ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out BasicAccountingInformation information, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
