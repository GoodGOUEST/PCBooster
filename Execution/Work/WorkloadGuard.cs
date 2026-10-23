// ============================================================
// PC Booster v3.6.9
// Execution/Work/WorkloadGuard.cs
// R17 日常模式保护（17.1 办公 / 17.2 浏览器 / 17.3 编译）
//   · 编译：工具链进程存在期间 + 退出后 30s 尾巴，免 Job 限速、免工作集修剪
//   · 办公：Office/WPS 前台期间（含切走后的 5 分钟窗口）全家免修剪、前台提 AboveNormal
//   · 浏览器：只有“安静超过 5 分钟”才允许修剪其子进程，
//             否则会砍掉正在后台播放音视频 / 跑 Web 应用的标签页
// 本类只做判定，不改任何系统状态（优先级提升在 ForegroundWatcher 回调节点完成）。
// ★ 分层：放在 Execution 层，因为它服务的是 Execution 的两个执行器
//   （AutoJobManager 限速、MemoryReclaimer 修剪）；Core 可以向下依赖 Execution。
// ============================================================
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
namespace PCBooster.Execution.Work
{
/// <summary>日常负载保护判定（编译 / 办公 / 浏览器）</summary>
public static class WorkloadGuard
{
    private static readonly object _lock = new();
    /// <summary>pid → 最近一次被看到在编译</summary>
    private static readonly Dictionary<int, DateTime> _buildPidSeen = new();
    /// <summary>工具链名 → 最近一次被看到（进程退出后靠它算 30s 尾巴）</summary>
    private static readonly Dictionary<string, DateTime> _buildTail = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastBuildAt = DateTime.MinValue;
    private static volatile string _lastBuildName = "";
    private static volatile string _officeForeground = "";
    private static DateTime _officeFgAt = DateTime.MinValue;
    private static DateTime _browserFgAt = DateTime.MinValue;
    /// <summary>前台切到办公/浏览器程序（钩子回调，比慢心跳快 10 秒）</summary>
    public static void OnForeground(int pid, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var now = DateTime.Now;
        if (ProcessMatcher.IsOffice(name)) { _officeFgAt = now; _officeForeground = name; }
        else if (ProcessMatcher.IsBrowserFamily(name)) _browserFgAt = now;
    }
    /// <summary>慢心跳刷新：编译工具链探测 + 前台兜底（钩子失效时也要准）</summary>
    public static void Refresh(NativeApi.ProcessSnapshot[] snapshot, int foregroundPid)
    {
        if (snapshot == null) return;
        var now = DateTime.Now;
        string fgName = "";
        bool buildSeen = false;
        foreach (var p in snapshot)
        {
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (p.Id == foregroundPid) fgName = p.ProcessName;
            if (!ProcessMatcher.IsBuildTool(p.ProcessName)) continue;
            buildSeen = true;
            _lastBuildName = p.ProcessName;
            lock (_lock)
            {
                _buildPidSeen[p.Id] = now;
                _buildTail[StringHelper.NormalizeProcessName(p.ProcessName)] = now;
            }
        }
        if (buildSeen) _lastBuildAt = now;
        if (!string.IsNullOrEmpty(fgName))
        {
            if (ProcessMatcher.IsOffice(fgName)) { _officeFgAt = now; _officeForeground = fgName; }
            else if (ProcessMatcher.IsBrowserFamily(fgName)) _browserFgAt = now;
        }
        Prune(now);
    }
    /// <summary>★ R17.3: 编译是否仍在进行（含退出后 WORKGUARD_BUILD_TAIL_SECONDS 尾巴）</summary>
    public static bool IsBuilding(DateTime now)
        => (now - _lastBuildAt).TotalSeconds <= Constants.WORKGUARD_BUILD_TAIL_SECONDS;
    /// <summary>★ R17.2/R17.3: 该进程是否禁止修剪工作集</summary>
    public static bool IsTrimExempt(int pid, string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var now = DateTime.Now;
        // 编译期：工具链不修剪（cl/csc 的 PDB 与增量状态被 Trim 掉后要回读磁盘）
        if (ProcessMatcher.IsBuildTool(name) && IsBuilding(now)) return true;
        // 浏览器：安静不足 5 分钟 → 任一浏览器家族进程都不修剪
        if (ProcessMatcher.IsBrowserFamily(name) &&
            (now - _browserFgAt).TotalMinutes < Constants.WORKGUARD_BROWSER_QUIET_MINUTES) return true;
        // 办公：前台办公窗口内，整个办公家族都不修剪（Excel 保存时别被 Trim）
        if (ProcessMatcher.IsOffice(name) &&
            (now - _officeFgAt).TotalMinutes < Constants.WORKGUARD_OFFICE_PROTECT_MINUTES) return true;
        return false;
    }
    /// <summary>★ R17.3: 该进程是否禁止施加 Job CPU/内存限制</summary>
    public static bool IsJobExempt(int pid, string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (ProcessMatcher.IsBuildTool(name)) return IsBuilding(DateTime.Now);
        if (ProcessMatcher.IsOffice(name) &&
            (DateTime.Now - _officeFgAt).TotalMinutes < Constants.WORKGUARD_OFFICE_PROTECT_MINUTES) return true;
        return false;
    }
    /// <summary>★ R17.1: 前台办公程序需要提优先级（保存/重算不排在后台队列后面）</summary>
    public static bool ShouldBoostForeground(string name)
        => !string.IsNullOrEmpty(name) && ProcessMatcher.IsOffice(name);
    /// <summary>当前保护状态（进程页展示，也用于日志）</summary>
    public static string GetStatus()
    {
        var now = DateTime.Now;
        if (IsBuilding(now))
        {
            int alive;
            lock (_lock) alive = _buildPidSeen.Keys.Count(pid => NativeApi.IsPidAliveCached(pid));
            string tail = alive == 0 ? "（收尾）" : "";
            string who = string.IsNullOrEmpty(_lastBuildName) ? "" : " · " + _lastBuildName;
            return $"🔨 编译中{tail}{who}";
        }
        double officeMin = (now - _officeFgAt).TotalMinutes;
        if (officeMin < Constants.WORKGUARD_OFFICE_PROTECT_MINUTES && !string.IsNullOrEmpty(_officeForeground))
            return $"📄 办公保护 · {_officeForeground}（余 {(int)(Constants.WORKGUARD_OFFICE_PROTECT_MINUTES - officeMin)} 分钟）";
        double browserMin = (now - _browserFgAt).TotalMinutes;
        if (browserMin < Constants.WORKGUARD_BROWSER_QUIET_MINUTES)
            return "🌐 浏览器活跃 · 后台标签页免修剪";
        return "— 无特殊保护";
    }
    /// <summary>退出前清理（避免下次启动把旧状态当成本次编译）</summary>
    public static void Reset()
    {
        lock (_lock) { _buildPidSeen.Clear(); _buildTail.Clear(); }
        _lastBuildAt = DateTime.MinValue;
        _lastBuildName = "";
        _officeForeground = "";
        _officeFgAt = DateTime.MinValue;
        _browserFgAt = DateTime.MinValue;
    }
    private static void Prune(DateTime now)
    {
        lock (_lock)
        {
            if (_buildPidSeen.Count > Constants.WORKGUARD_MAX_ENTRIES)
            {
                var dead = _buildPidSeen.Keys
                    .Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
                foreach (var pid in dead) _buildPidSeen.Remove(pid);
            }
            if (_buildTail.Count > Constants.WORKGUARD_MAX_ENTRIES)
            {
                var stale = _buildTail
                    .Where(kv => (now - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _buildTail.Remove(k);
            }
        }
    }
}
}
