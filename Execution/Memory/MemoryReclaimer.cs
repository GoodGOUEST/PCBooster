// ============================================================
// PC Booster v3.6.8
// Execution/Memory/MemoryReclaimer.cs
// ①根治: 生命迹象三重门控（WS增长/30s峰值CPU/近5分钟前台）+ 温和修剪
//        全清仅紧急+冷+>1GB；零名字豁免
// ============================================================
using System.Threading;
using PCBooster.Execution.Preload;
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;
namespace PCBooster.Execution.Memory
{
/// <summary>内存回收器（激进但只对准真废弃进程）</summary>
public static class MemoryReclaimer
{
private static int _isReclaiming;
private static int _totalReclaims;
private static long _totalBytesFreed;
    private static readonly Dictionary<int, DateTime> _lastTrim = new();
    private static readonly Dictionary<int, DateTime> _fgSeen = new();
    public static long ReclaimMemory(List<int> preloadPids, int foregroundPid, bool isEmergency = false)
    {
        // ★ 3.6.8: 总开关（默认关）——Windows 自带 standby list/内存压缩，强制 Trim 往往制造二次卡顿
        if (!AppConfig.EnableMemoryReclaim) return 0;
        if (!Resilience.TryAcquire(ref _isReclaiming)) return 0;
        try
        {
            var (_, mem) = CacheManager.GetSystemState();
            bool emergency = isEmergency || mem < Constants.RECLAIM_EMERGENCY_PRESSURE;
            bool normal = emergency || mem < Constants.RECLAIM_NORMAL_PRESSURE;
            bool gentle = normal || mem < Constants.RECLAIM_GENTLE_PRESSURE;
            if (!gentle) return 0;
            var now = DateTime.Now;
            if (foregroundPid > 0) _fgSeen[foregroundPid] = now;
            PruneFgSeen(now);
            PruneLastTrim(now);
            HashSet<string> hotNames = null;
            if (!emergency)
            {
                hotNames = new HashSet<string>();
                foreach (var (process, confidence) in IntelligenceEngine.Predict(15))
                    if (confidence > 0.6) hotNames.Add(StringHelper.NormalizeProcessName(process));
            }
            var procs = NativeApi.GetSnapshotCached();
            var candidates = GetCandidates(procs, preloadPids, foregroundPid,
                hotNames, normal, emergency, now);
            if (candidates.Count == 0) return 0;
            int limit = emergency ? Constants.RECLAIM_LIMIT_EMERGENCY
                        : normal ? Constants.RECLAIM_LIMIT_NORMAL : Constants.RECLAIM_LIMIT_GENTLE;
            long totalFreed = 0; int reclaimed = 0;
            foreach (var (pid, cold, warm, ws) in candidates.Take(limit))
            {
                long freed = ApplyTrim(pid, cold, warm, ws, emergency, normal);
                if (freed > 0) { totalFreed += freed; reclaimed++; _lastTrim[pid] = now; }
                Thread.Sleep(Constants.RECLAIM_TRIM_INTERVAL_MS);
            }
            if (reclaimed > 0)
            {
                _totalReclaims += reclaimed;
                _totalBytesFreed += totalFreed;
                AppLogger.Info($"🧹 内存回收[{TierName(emergency, normal)}]: " +
                    $"{reclaimed} 进程, 释放 {totalFreed / 1024 / 1024}MB");
            }
            return totalFreed;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"内存回收异常: {ex.Message}");
            Resilience.ReportModuleFault(ModuleRegistry.Names.MemoryReclaimer);
            return 0;
        }
        finally { Resilience.Release(ref _isReclaiming); }
    }
    private static List<(int pid, bool cold, bool warm, long ws)> GetCandidates(
        NativeApi.ProcessSnapshot[] procs, List<int> preloadPids, int foregroundPid,
        HashSet<string> hotNames, bool allowWarm, bool emergency, DateTime now)
    {
        var result = new List<(int pid, bool cold, bool warm, long ws, int tier)>();
        var preloadSet = new HashSet<int>(preloadPids ?? new List<int>());
        foreach (var p in procs)
        {
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
            // ★ 3.6.8 红线: 反作弊进程不修剪（原仅预加载保护/Job 保护两层）
            if (ProcessMatcher.IsProtected(p.ProcessName)) continue;
            // ★ 本轮修复: 游戏工作集被 Trim 后，切回游戏要从磁盘重新读回（卡顿几秒）。
            //   即使已经切到后台、不满足“最近前台”条件，也一律不碰。
            if (ProcessMatcher.IsGame(p.ProcessName)) continue;
            if (p.Id == foregroundPid) continue;
            if (preloadSet.Contains(p.Id)) continue;
            if (PreloadProtection.IsProtected(p.Id)) continue;
            if (Jobs.AutoJobManager.IsJobProtected(p.Id)) continue;
            // ★ R17: 编译工具链 / 办公保护窗口 / 浏览器活跃期 一律不修剪
            if (Work.WorkloadGuard.IsTrimExempt(p.Id, p.ProcessName)) continue;
            // ★ R17 卡死修复: 有可见窗口的程序（含未识别的游戏）不 Trim——
            //   把它的工作集抽走，切回来要从磁盘回读，体感就是“卡一下/卡死”。
            if (ProcessSafeHelper.HasVisibleWindow(p.Id)) continue;
            var strategy = IntelligenceEngine.GetStrategy(p.ProcessName);
            if (!strategy.CanReclaim) continue;
            if (_fgSeen.TryGetValue(p.Id, out var fgAt) &&
                (now - fgAt).TotalSeconds < Constants.RECLAIM_RECENT_FG_SECONDS) continue;
            int cooldown = emergency ? Constants.RECLAIM_COOLDOWN_EMERGENCY_SECONDS
                                     : Constants.RECLAIM_COOLDOWN_SECONDS;
            if (_lastTrim.TryGetValue(p.Id, out var last) &&
                (now - last).TotalSeconds < cooldown) continue;
            if (NativeApi.IsWsGrowingCached(p.Id)) continue; // 生命迹象①
            double maxCpu = NativeApi.GetProcessCpuMax30Cached(p.Id); // 生命迹象②
            bool cold = maxCpu < Constants.RECLAIM_COLD_MAX_CPU30;
            bool warm = !cold && maxCpu < Constants.RECLAIM_WARM_MAX_CPU30 &&
                        p.WorkingSet64 > Constants.RECLAIM_BLOAT_WS;
            if (!cold && !(warm && allowWarm)) continue;
            if (hotNames != null && hotNames.Contains(
                StringHelper.NormalizeProcessName(p.ProcessName))) continue;
            result.Add((p.Id, cold, warm, p.WorkingSet64, cold ? 0 : 1));
        }
        return result
            .OrderBy(r => r.tier)
            .ThenByDescending(r => r.ws)
            .Select(r => (r.pid, r.cold, r.warm, r.ws)).ToList();
    }
    private static long ApplyTrim(int pid, bool cold, bool warm, long ws, bool emergency, bool normal)
    {
        if (ws <= 0) return 0;
        long floor = Constants.RECLAIM_TRIM_FLOOR_MB * 1024 * 1024;
        bool ok;
        if (emergency && cold && ws > Constants.RECLAIM_FULL_CLEAR_MIN_WS)
            ok = NativeApi.UnlockProcessMemory(pid); // 唯一全清路径
        else
        {
            double ratio =
                !normal ? Constants.RECLAIM_RATIO_GENTLE :
                emergency ? (cold ? Constants.RECLAIM_RATIO_EMERGENCY_COLD : Constants.RECLAIM_RATIO_EMERGENCY_WARM) :
                (cold ? Constants.RECLAIM_RATIO_NORMAL_COLD : Constants.RECLAIM_RATIO_NORMAL_WARM);
            ok = NativeApi.TrimWorkingSet(pid, Math.Max(floor, (long)(ws * ratio)));
        }
        if (!ok) return 0;
        long after = ProcessSafeHelper.GetWorkingSet(pid);
        return Math.Max(0, ws - after);
    }
    private static string TierName(bool emergency, bool normal)
        => emergency ? "紧急" : normal ? "正常" : "温和";
    private static void PruneLastTrim(DateTime now)
    {
        if (_lastTrim.Count == 0) return;
        var stale = _lastTrim.Where(kv => (now - kv.Value).TotalMinutes > Constants.RECLAIM_TRIM_RETENTION_MINUTES)
                             .Select(kv => kv.Key).ToList();
        foreach (var k in stale) _lastTrim.Remove(k);
    }
    private static void PruneFgSeen(DateTime now)
    {
        if (_fgSeen.Count < 64) return;
        var stale = _fgSeen.Where(kv => (now - kv.Value).TotalSeconds > 600)
                           .Select(kv => kv.Key).ToList();
        foreach (var k in stale) _fgSeen.Remove(k);
    }
    public static string GetStats()
        => $"🧹 内存回收: {_totalReclaims} 次, 释放 {_totalBytesFreed / 1024 / 1024}MB";
}
}