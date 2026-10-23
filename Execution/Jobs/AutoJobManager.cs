// ============================================================
// PC Booster v3.6.8
// Execution/Jobs/AutoJobManager.cs
// ②根治: -1→0（不写该限制），删除隐式 50/2048 回退帽；已限制跳过；清理无条件化
// ============================================================
using PCBooster.Core;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;
namespace PCBooster.Execution.Jobs
{
/// <summary>自动 Job 管理器（纯策略执行器）</summary>
public static class AutoJobManager
{
private static int _isApplying;
private static int _totalApplied;
private static readonly HashSet<int> _jobProtectedPids = new();
private static readonly object _jobLock = new();
// ★ 3.6.8 修复: 施加失败冷却（避免每轮重复尝试同 PID 刷日志）
private static readonly Dictionary<int, DateTime> _failUntil = new();
private static readonly object _failCoolLock = new();
// ★ 3.6.8: 前台观测表——刚切到后台的进程不施加限制
private static readonly Dictionary<int, DateTime> _fgSeen = new();
private static readonly object _fgLock = new();
// ★ 3.6.8: 单进程吃满一个核的连续计数（滞回）
private static readonly Dictionary<int, int> _hogStreak = new();
private static readonly object _hogLock = new();
/// <summary>★ 3.6.8: 当前被自动限速的进程数（UI 展示用）</summary>
public static int AutoLimitedCount { get { lock (_jobLock) return _jobProtectedPids.Count; } }
    public static void CheckAndApplyJobs(SchedulerContext context)
    {
        JobObjectApi.CleanupExitedProcesses(); // ★ 无条件清理
        CleanupDeadProtectedPids();
        if (!AppConfig.EnableAutoJob) return;
        if (!Resilience.TryAcquire(ref _isApplying)) return;
        try
        {
            var (cpu, mem) = Infrastructure.Cache.CacheManager.GetSystemState();
            var procs = NativeApi.GetSnapshotCached();
            int fgPid = context?.ForegroundPid ?? 0;
            // ★ 3.6.8: 记录前台并立即解除它身上的 Job 限制（切回应用要马上恢复流畅）
            TrackForeground(fgPid);
            // ★ 3.6.8: 游戏场景采用更激进的后台限速档
            bool gaming = string.Equals(context?.Scene, Constants.SCENE_GAMING, StringComparison.OrdinalIgnoreCase);
            // ★ 3.6.8 修复: 判据由“整机 CPU 70%”改为“单进程持续吃满一个核”
            //（8 核机器上一个后台进程吃满单核只有 12.5%，旧判据永不触发）
            int hogs = CountHogs(procs, fgPid, gaming ? Constants.AUTOJOB_HOG_CPU_GAME : Constants.AUTOJOB_HOG_CPU);
            bool overLoad = hogs > 0 || mem <= Constants.AUTOJOB_MEM_TRIGGER;
            if (!overLoad)
            {
                // ★ 3.6.8 修复: 负载回落后解除已施加的限制（带滞回，避免临界抖动）
                if (cpu <= Constants.AUTOJOB_RELEASE_CPU && mem >= Constants.AUTOJOB_RELEASE_MEM)
                    ReleaseAllAutoLimits();
                return;
            }
            var targets = GetTargets(procs, fgPid);
            int maxTargets = (int)AppConfig.GetDouble(
                gaming ? ConfigSchema.AutoJobMaxTargetsGame : ConfigSchema.AutoJobMaxTargets,
                gaming ? Constants.AUTOJOB_MAX_TARGETS_GAME : Constants.AUTOJOB_MAX_TARGETS);
            foreach (var target in targets.Take(maxTargets))
                ApplyJobLimits(target);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"AutoJob 异常: {ex.Message}");
            Resilience.ReportModuleFault(ModuleRegistry.Names.AutoJobManager);
        }
        finally { Resilience.Release(ref _isApplying); }
    }
    /// <summary>★ 3.6.8: 统计“持续吃满一个核”的进程数（GetProcessCpuCached 口径为 0~100 = 一个核）</summary>
    private static int CountHogs(NativeApi.ProcessSnapshot[] procs, int foregroundPid, double hogCpu)
    {
        int count = 0;
        foreach (var p in procs)
        {
            if (p.Id <= 0 || p.Id == foregroundPid) continue;
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
            // ★ R17.3: 编译器吃满一个核是正常工作，不能算“后台抢占”，
            //   否则一编译就触发全局限速风暴，把后台/其它工具链一起压下去。
            if (ProcessMatcher.IsBuildTool(p.ProcessName)) continue;
            double pc = NativeApi.GetProcessCpuCached(p.Id);
            if (pc < hogCpu)
            {
                lock (_hogLock) _hogStreak.Remove(p.Id);
                continue;
            }
            int streak;
            lock (_hogLock)
            {
                _hogStreak.TryGetValue(p.Id, out streak);
                streak++;
                _hogStreak[p.Id] = streak;
            }
            if (streak >= Constants.AUTOJOB_HOG_STREAK) count++;
        }
        // ★ 本轮修复: 以 PID 为键的三张表以前只增不减，长跑（挂机/几天不关）会缓慢增长，
        //   PID 复用后旧状态还会误判。超过 512 条时按“当前快照里不存在的 PID”清理。
        if (_hogStreak.Count > 512 || _failUntil.Count > 512 || _fgSeen.Count > 512)
            PruneState(procs);
        return count;
    }

    /// <summary>清理已退出进程留下的状态（保留当前仍存活 PID 的记录）</summary>
    private static void PruneState(NativeApi.ProcessSnapshot[] procs)
    {
        var alive = new HashSet<int>();
        foreach (var p in procs) alive.Add(p.Id);
        lock (_hogLock)
        {
            foreach (var pid in _hogStreak.Keys.Where(k => !alive.Contains(k)).ToList()) _hogStreak.Remove(pid);
            foreach (var pid in _failUntil.Keys.Where(k => !alive.Contains(k)).ToList()) _failUntil.Remove(pid);
            foreach (var pid in _fgSeen.Keys.Where(k => !alive.Contains(k)).ToList()) _fgSeen.Remove(pid);
        }
    }
    private static List<NativeApi.ProcessSnapshot> GetTargets(
        NativeApi.ProcessSnapshot[] procs, int foregroundPid)
    {
        var result = new List<NativeApi.ProcessSnapshot>();
        foreach (var p in procs)
        {
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
            if (ProcessMatcher.IsProtected(p.ProcessName)) continue;   // ★ 3.6.8 红线: 反作弊绝不施加 Job
            if (p.Id == foregroundPid) continue;
            // ★ 3.6.8: 用户交互类（浏览器/Office/开发/会议/设计）不做 Job 限速
            if (IsUserFacing(p.ProcessName)) continue;
            // ★ R17 游戏卡死修复: 有可见窗口的程序（含未被目录识别的游戏/客户端）一律不限速。
            //   实测日志里 Games / PlatformProcess 被反复施加 Job CPU 限制，
            //   一旦命中游戏主进程，就是“卡住/黑屏”级后果。
            if (ProcessSafeHelper.HasVisibleWindow(p.Id)) continue;
            // ★ 3.6.8: 刚切到后台 5 分钟内的进程不碰（避免“临时切后台就被限速”）
            if (WasRecentlyForeground(p.Id)) continue;
            if (Preload.PreloadProtection.IsProtected(p.Id)) continue;
            if (IsJobProtected(p.Id)) continue;
            // ★ R17.3: 编译期/办公保护窗口内不施加 Job 限速
            if (Work.WorkloadGuard.IsJobExempt(p.Id, p.ProcessName)) continue;
            var strategy = IntelligenceEngine.GetStrategy(p.ProcessName);
            if (strategy.JobCpuLimit == -1 && strategy.JobMemoryLimitMB == -1) continue;
            if (p.WorkingSet64 < Constants.AUTOJOB_MIN_WORKING_SET_MB * 1024 * 1024) continue;
            result.Add(p);
        }
        return result.OrderByDescending(p => p.WorkingSet64).ToList();
    }
    private static void ApplyJobLimits(NativeApi.ProcessSnapshot p)
    {
        var strategy = IntelligenceEngine.GetStrategy(p.ProcessName);
        int cpuLimit = strategy.JobCpuLimit > 0 ? strategy.JobCpuLimit : 0;   // ★ ②
        int memLimit = strategy.JobMemoryLimitMB > 0 ? strategy.JobMemoryLimitMB : 0; // ★ ②
        if (cpuLimit == 0 && memLimit == 0) return;
        lock (_failCoolLock)
            if (_failUntil.TryGetValue(p.Id, out var until) && until > DateTime.Now) return;
        var limits = new JobObjectApi.JobObjectLimits
        {
            MaxCpuPercent = cpuLimit,
            MaxMemoryMB = memLimit,
            IoPriority = strategy.IoPriority
        };
        if (JobObjectApi.ApplyJobLimitsToProcess(p.Id, limits))
        {
            _totalApplied++;
            lock (_jobLock) _jobProtectedPids.Add(p.Id);
            AppLogger.Debug($"⚙ Job 限制: {p.ProcessName} (CPU={cpuLimit}%, MEM={memLimit}MB)");
        }
        else
        {
            // ★ 3.6.8: 失败进入冷却（多为进程已在其他 Job 中，Windows 限制嵌套）
            lock (_failCoolLock) _failUntil[p.Id] = DateTime.Now.AddSeconds(Constants.AUTOJOB_FAIL_COOLDOWN_SECONDS);
            AppLogger.Debug($"⚙ Job 施加失败: {p.ProcessName}（已在其他 Job 或权限不足），{Constants.AUTOJOB_FAIL_COOLDOWN_SECONDS}s 内不重试");
        }
    }
    /// <summary>记录前台进程并立即解除其 Job 限制（切回应用应马上恢复）</summary>
    private static void TrackForeground(int fgPid)
    {
        if (fgPid <= 0) return;
        lock (_fgLock) _fgSeen[fgPid] = DateTime.Now;
        ReleaseJob(fgPid);
        if (_fgSeen.Count > 128)
        {
            lock (_fgLock)
            {
                var gone = _fgSeen.Keys.Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
                foreach (var pid in gone) _fgSeen.Remove(pid);
            }
        }
    }

    /// <summary>★ 3.6.8: 刚切到后台的进程给它 5 分钟缓冲，避免“临时切后台就被限速/崩”</summary>
    private static bool WasRecentlyForeground(int pid)
    {
        lock (_fgLock)
            return _fgSeen.TryGetValue(pid, out var at) &&
                   (DateTime.Now - at).TotalSeconds < Constants.AUTOJOB_RECENT_FG_SECONDS;
    }

    /// <summary>用户交互类程序：不做 Job 限速（多窗口浏览器最典型）</summary>
    private static bool IsUserFacing(string name)
        => ProcessMatcher.IsBrowser(name) || ProcessMatcher.IsDevTool(name) ||
           ProcessMatcher.IsOffice(name) || ProcessMatcher.IsMeetingTool(name) ||
           ProcessMatcher.IsDesignTool(name) || ProcessMatcher.IsVideoEditor(name);

    private static void CleanupDeadProtectedPids()
    {
        lock (_jobLock)
        {
            var dead = _jobProtectedPids.Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
            foreach (var pid in dead) _jobProtectedPids.Remove(pid);
            // ★ 3.6.8: 连续计数表也要清理，避免 PID 复用后继承旧 streak
            lock (_hogLock)
            {
                var gone = _hogStreak.Keys.Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
                foreach (var pid in gone) _hogStreak.Remove(pid);
            }
        }
    }
    public static bool IsJobProtected(int pid)
    { lock (_jobLock) return _jobProtectedPids.Contains(pid); }
    /// <summary>★ 3.6.8 修复: 负载回落时批量解除自动施加的 Job 限制
    ///（原实现只增不减，被限进程会一直带着 CPU/内存帽运行到退出）</summary>
    private static void ReleaseAllAutoLimits()
    {
        List<int> pids;
        lock (_jobLock)
        {
            if (_jobProtectedPids.Count == 0) return;
            pids = _jobProtectedPids.ToList();
            _jobProtectedPids.Clear();
        }
        foreach (int pid in pids)
        {
            JobObjectApi.ReleaseJob(pid);
            lock (_failCoolLock) _failUntil.Remove(pid);
        }
        AppLogger.Info($"🔓 负载回落: 已解除 {pids.Count} 个 Job 限制");
    }
    public static void ReleaseJob(int pid)
    {
        JobObjectApi.ReleaseJob(pid);
        lock (_jobLock) _jobProtectedPids.Remove(pid);
    }

    /// <summary>★ 本轮新增: 一键还原时批量解除所有自动限速（含内部跟踪集合）</summary>
    public static void ReleaseAllLimits() => ReleaseAllAutoLimits();
    public static string GetStats()
        => $"⚙ AutoJob: {_totalApplied} 次限制, {_jobProtectedPids.Count} 个受保护";
}
}