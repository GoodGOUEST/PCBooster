// ============================================================
// PC Booster v3.6.8
// Execution/Jobs/JobObjectApi.cs
// 修复: D7 默认值 0（不写该限制，杜绝隐式硬帽）/ 句柄三道闸防泄漏 /
//       清理走 IsPidAliveCached
// ============================================================
using System.Runtime.InteropServices;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
namespace PCBooster.Execution.Jobs
{
/// <summary>Job Object API 封装</summary>
public static class JobObjectApi
{
public class JobObjectLimits
{
public int MaxCpuPercent { get; set; } = 0;   // ★ D7: 0 = 不写该限制
public int MaxMemoryMB { get; set; } = 0;     // ★ D7
public string IoPriority { get; set; } = "Normal";
}
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public IntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x00000001;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x00000004;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 11;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_TERMINATE = 0x0001;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    // ★ 3.6.8: 后台 IO/CPU 模式（原 JobObjectLimits.IoPriority 只传不使用）
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);
    private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
    private const uint PROCESS_MODE_BACKGROUND_END = 0x00200000;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private static readonly Dictionary<int, IntPtr> _jobHandles = new();
    private static readonly object _lock = new();
    public static bool ApplyJobLimitsToProcess(int pid, JobObjectLimits limits)
    {
        IntPtr hJob = IntPtr.Zero, hProcess = IntPtr.Zero;
        bool assigned = false;
        try
        {
            if (!AppEnvironment.IsElevated) return false;
            hJob = CreateJobObject(IntPtr.Zero, $"PCBooster_Job_{pid}");
            if (hJob == IntPtr.Zero) return false;
            if (limits.MaxMemoryMB > 0)
            {
                // ★ 3.6.8 安全闸: 给“已经超过上限”的进程补提交帽，会让它之后的分配全部失败
                //   （浏览器/大型软件被误杀的根因）。宁可拒绝，也不提交内存帽。
                long wsMb = ProcessSafeHelper.GetWorkingSet(pid) / (1024 * 1024);
                if (wsMb > (long)limits.MaxMemoryMB * 0.8)
                {
                    AppLogger.Warning($"拒绝设置提交帽 PID={pid}: 当前工作集 {wsMb}MB 已接近上限 {limits.MaxMemoryMB}MB");
                    return false;
                }
                var memInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    { LimitFlags = JOB_OBJECT_LIMIT_JOB_MEMORY },
                    JobMemoryLimit = new IntPtr((long)limits.MaxMemoryMB * 1024 * 1024)
                };
                if (!SetJobInfo(hJob, JobObjectExtendedLimitInformation, memInfo))
                {
                    AppLogger.Warning($"Job 内存限制设置失败 PID={pid}（HRESULT 非 0）");
                    return false;
                }
            }
            if (limits.MaxCpuPercent > 0)
            {
                var cpuInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate = (uint)(limits.MaxCpuPercent * 100)
                };
                if (!SetJobInfo(hJob, JobObjectCpuRateControlInformation, cpuInfo))
                {
                    AppLogger.Warning($"Job CPU 限制设置失败 PID={pid}（HRESULT 非 0）");
                    return false;
                }
            }
            // ★ 3.6.8 修复: 少 PROCESS_SET_INFORMATION 会导致 IoPriority=Low
            //   的“后台模式”静默失败（SetPriorityClass 需要该权限）
            hProcess = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_SET_INFORMATION,
                false, pid);
            if (hProcess == IntPtr.Zero) return false;
            assigned = AssignProcessToJobObject(hJob, hProcess);
            if (!assigned)
                // ★ 3.6.8 修复: 原先静默失败（只写 Debug），用户点"应用"无任何反馈
                AppLogger.Warning($"Job 施加失败 PID={pid}：进程可能已属于其他 Job（Windows 嵌套限制）或权限不足");
            if (assigned)
            {
                // ★ 3.6.8: 兑现 IoPriority（Low = 后台 IO/CPU 模式，降低磁盘与 CPU 抢占）
                if (string.Equals(limits.IoPriority, "Low", StringComparison.OrdinalIgnoreCase))
                    SetPriorityClass(hProcess, PROCESS_MODE_BACKGROUND_BEGIN);
                lock (_lock)
                {
                    if (_jobHandles.TryGetValue(pid, out var old) && old != IntPtr.Zero && old != hJob)
                        CloseHandle(old); // 覆盖前关旧
                    _jobHandles[pid] = hJob;
                }
            }
            return assigned;
        }
        catch (Exception ex) { AppLogger.Debug($"Job 限制失败 PID={pid}: {ex.Message}"); return false; }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            if (!assigned && hJob != IntPtr.Zero) CloseHandle(hJob); // 失败兜底
        }
    }
    /// <summary>结构体 → 非托管内存 → SetInformationJobObject（返回真实成败）</summary>
    private static bool SetJobInfo<T>(IntPtr hJob, int infoClass, T info) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(hJob, infoClass, ptr, (uint)size);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"SetInformationJobObject 异常: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static bool ReleaseJob(int pid)
    {
        bool found = false;
        lock (_lock)
        {
            if (_jobHandles.TryGetValue(pid, out var hJob))
            {
                CloseHandle(hJob);
                _jobHandles.Remove(pid);
                found = true;
            }
        }
        // ★ 3.6.8: 退出后台模式（锁外做系统调用）
        if (found)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try { SetPriorityClass(h, PROCESS_MODE_BACKGROUND_END); } catch { }
                CloseHandle(h);
            }
        }
        return found;
    }
    public static void CleanupExitedProcesses()
    {
        lock (_lock)
        {
            var exited = _jobHandles.Keys
                .Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
            foreach (var pid in exited)
            { CloseHandle(_jobHandles[pid]); _jobHandles.Remove(pid); }
        }
    }
    public static void ReleaseAll()
    {
        // ★ 3.6.8 修复: 原来只关句柄，被设为 PROCESS_MODE_BACKGROUND_BEGIN 的进程
        //   即使 Job 被销毁仍会停留在后台节流模式，直到进程退出。
        List<int> pids;
        lock (_lock) pids = _jobHandles.Keys.ToList();
        foreach (var pid in pids) ReleaseJob(pid);
    }
    public static int GetJobCount() { lock (_lock) return _jobHandles.Count; }
    public static string GetStats() => $"⚙ Job Object: {GetJobCount()} 个活跃";
}
}