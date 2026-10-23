// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Profiling/InstructionSetSuite.cs
// 指令集调度套件（CpuTopology + AffinityResolver + CacheAware + PerfCounter）
// ============================================================
using PCBooster.Infrastructure.Common;
namespace PCBooster.Infrastructure.Profiling
{
/// <summary>CPU 拓扑检测（异步 WMI，>64 核保护）</summary>
public static class CpuTopology
{
    public class CoreInfo { public int PhysicalCoreId; public List<int> LogicalProcessors = new(); }
    private static readonly List<CoreInfo> _cores = new();
    private static int _physicalCoreCount, _logicalCoreCount;
    private static bool _hasSmt;
    private static volatile bool _detected;
    private static bool _detecting;   // 区分“正在检测/已完成”，避免并发调用読到空拓扑
    private static readonly object _lock = new();
    public static void Detect()
    {
        lock (_lock)
        {
            if (_detected || _detecting) return;
            _detecting = true;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    if (!_detected)
                    {
                        if (!TryDetectViaWmi()) FallbackDetection();
                        _hasSmt = _logicalCoreCount > _physicalCoreCount;
                        if (_logicalCoreCount > 64)
                            AppLogger.Warning($"⚠️ {_logicalCoreCount} 逻辑核，亲和性管理限制前 64 核");
                        AppLogger.Info($"📐 CPU 拓扑: {_physicalCoreCount}P/{_logicalCoreCount}L, SMT={_hasSmt}");
                        // 扩展拓扑（L3 域 / 混合架构）——掩码决策要用
                        CpuTopologyEx.Detect();
                        AppLogger.Info($"📐 {CpuTopologyEx.Describe()} · 游戏核掩码 0x{CpuTopologyEx.GetGameMask(GetRecommendedGameCores()):X}");
                    }
                }
                finally
                {
                    _detected = true;
                    _detecting = false;
                }
            }
        });
    }
    private static bool TryDetectViaWmi()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            int phys = 0, logi = 0;
            foreach (var obj in searcher.Get())
            {
                phys += Convert.ToInt32(obj["NumberOfCores"]);
                logi += Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
            }
            if (phys <= 0 || logi <= 0) return false;
            _physicalCoreCount = phys; _logicalCoreCount = logi;
            int smt = logi / phys;
            for (int i = 0; i < phys; i++)
            {
                var core = new CoreInfo { PhysicalCoreId = i };
                for (int s = 0; s < smt; s++) core.LogicalProcessors.Add(i * smt + s);
                _cores.Add(core);
            }
            return true;
        }
        catch { return false; }
    }
    private static void FallbackDetection()
    {
        // WMI 不可用时也先试一次 Win32 API（GetLogicalProcessorInformationEx）——
        // Environment.ProcessorCount 会被作业/CPU 集限制带偏（实测在受限环境里报 8
        // 而实际是 32），拿它当真会把游戏只绑到 2 个核上。
        CpuTopologyEx.Detect();
        if (CpuTopologyEx.IsReady)
        {
            _logicalCoreCount = CpuTopologyEx.LogicalCount;
            _physicalCoreCount = CpuTopologyEx.CoreCount;
        }
        else
        {
            _logicalCoreCount = Environment.ProcessorCount;
            _physicalCoreCount = _logicalCoreCount % 2 == 0 && _logicalCoreCount >= 4 ? _logicalCoreCount / 2 : _logicalCoreCount;
        }
        int smt = Math.Max(1, _logicalCoreCount / Math.Max(1, _physicalCoreCount));
        for (int i = 0; i < _physicalCoreCount; i++)
        {
            var core = new CoreInfo { PhysicalCoreId = i };
            for (int s = 0; s < smt; s++) core.LogicalProcessors.Add(i * smt + s);
            _cores.Add(core);
        }
    }
    public static int PhysicalCoreCount { get { lock (_lock) return _physicalCoreCount; } }
    public static int LogicalCoreCount { get { lock (_lock) return _logicalCoreCount; } }
    public static bool HasSMT { get { lock (_lock) return _hasSmt; } }
    /// <summary>多核低频平台（Xeon E5 / 线程撕裂者等）</summary>
    public static bool IsManyCore => PhysicalCoreCount >= Constants.MANYCORE_PHYSICAL_THRESHOLD;
    /// <summary>E5 等低频多核：宁少勿多，4~8 个物理核换更高全核睿频
    /// 手动值必须给后台至少留 2 个物理核。
    /// 原来允许 GameCoreCount = 物理核数（如 16/16），此时“后台核”= 0，
    /// GetBackgroundMask() 返回 0 → 核心隔离永远不生效（页面显示未启用）。</summary>
    public static int GetRecommendedGameCores()
    {
        int phys = PhysicalCoreCount;
        // 基础拓扑可能因 WMI 不可用而降级，扩展拓扑（Win32 API）更可信
        if (CpuTopologyEx.IsReady && CpuTopologyEx.CoreCount > phys) phys = CpuTopologyEx.CoreCount;
        if (phys <= 0) return 1;
        // 核心隔离开启时，必须给后台至少留 2 个物理核；关闭隔离时允许全给（反正不需要后台核）
        bool isolationOn = AppConfig.GetBool("EnableInstructionSetScheduling", true);
        int maxManual = isolationOn ? Math.Max(1, phys - 2) : phys;
        // 允许配置覆盖（0=自动）；E5 上可手动试 4 / 6 / 8
        int manual = (int)AppConfig.GetDouble(ConfigSchema.GameCoreCount, 0);
        if (manual > 0 && manual <= maxManual) return manual;
        if (phys >= Constants.MANYCORE_PHYSICAL_THRESHOLD)
            // 原来用 phys/4（16 核→只给 4 核）。实测这类平台 1 核与 12 核的
            // 单线程速度差 ≤0.5%（睿频曲线很平，挤不出频率），少给核反而是纯损失。
            return Math.Clamp(phys / 2, Constants.MANYCORE_GAME_CORES_MIN,
                Constants.MANYCORE_GAME_CORES_MAX);
        return Math.Max(1, phys / Constants.GAME_CORE_DIVISOR);
    }
    public static string GetProfileDescription()
    {
        int phys = PhysicalCoreCount, logi = LogicalCoreCount;
        if (phys <= 0) return "检测中…";
        int game = GetRecommendedGameCores();
        string mode = IsManyCore ? "多核低频策略" : "常规策略";
        return $"{phys}P/{logi}L, SMT={HasSMT}, {mode} {game}C/{game * (HasSMT ? 2 : 1)}T";
    }
    // GetPhysicalCoreMask 已删除（只有旧的 CacheAware 2 核逻辑在用，
    // 现改为同 L3 域掩码，见 CpuTopologyEx.GetGameMask）。
    public static (ulong heavyMask, ulong backgroundMask) GetIsolationMasks(int heavyCores)
    {
        lock (_lock)
        {
            // 重核数必须小于核数，否则后台核为 0，隔离形同未启用
            if (_cores.Count <= 0) return (0, 0);
            heavyCores = Math.Clamp(heavyCores, 1, Math.Max(1, _cores.Count - 1));
            ulong heavy = 0, bg = 0;
            for (int i = 0; i < _cores.Count; i++)
            {
                ulong coreMask = 0;
                foreach (int lp in _cores[i].LogicalProcessors)
                    if (lp < 64) coreMask |= (1UL << lp);
                if (i < heavyCores) heavy |= coreMask; else bg |= coreMask;
            }
            return (heavy, bg);
        }
    }
}
/// <summary>亲和性 4 源优先级合并</summary>
public static class AffinityResolver
{
    public static ulong Resolve(int pid, string processName, ulong? userRuleMask = null, ulong? instructionSetMask = null, ulong? selfLearningMask = null)
    {
        if (userRuleMask.HasValue && userRuleMask.Value != 0) return userRuleMask.Value;
        if (instructionSetMask.HasValue && instructionSetMask.Value != 0) return instructionSetMask.Value;
        if (selfLearningMask.HasValue && selfLearningMask.Value != 0) return selfLearningMask.Value;
        return GetDefaultMask();
    }
    public static bool ApplyAffinity(int pid, string processName, ulong? userRuleMask = null, ulong? instructionSetMask = null, ulong? selfLearningMask = null)
    {
        // >64 逻辑核会分处理器组，Process.ProcessorAffinity 只作用于进程所在组，
        // 而我们的掩码按组内位图构造；跨组写入可能把亲和性设错，宁可跳过。
        if (CpuTopology.LogicalCoreCount > 64)
        {
            LogGroupLimitOnce();
            return false;
        }
        ulong final = Resolve(pid, processName, userRuleMask, instructionSetMask, selfLearningMask);
        if (final == 0) return false;
        return ProcessSafeHelper.SetAffinity(pid, final);
    }
    private static bool _groupLimitLogged;
    private static void LogGroupLimitOnce()
    {
        if (_groupLimitLogged) return;
        _groupLimitLogged = true;
        AppLogger.Warning("⚠️ 逻辑核 >64（多处理器组），当前版本跳过亲和性优化以避免跨组误设");
    }
    public static ulong GetDefaultMask()
    {
        // 未检测完成时不要返回 64 位全 1（在 16/32 逻辑核机器上
        // 会包含不存在的核位，SetProcessAffinityMask 直接失败）
        int count = CpuTopology.LogicalCoreCount;
        if (count <= 0) count = Environment.ProcessorCount;
        count = Math.Min(count, 64);
        if (count <= 0) return ulong.MaxValue;
        return count == 64 ? ulong.MaxValue : ((1UL << count) - 1);
    }
    /// <summary>重负载场景隔离掩码（游戏/设计/开发适用）
    /// 多核低频平台改用 4~8 物理核（含 SMT 线程），而不是一半物理核</summary>
    public static ulong GetHeavyLoadIsolationMask()
    {
        int heavyCores = CpuTopology.GetRecommendedGameCores();
        // 优先用 L3 域/混合架构感知的掩码（同一 CCD/环 内选核）
        if (CpuTopologyEx.IsReady)
        {
            // 可选开关：单 L3 域平台（如双环至强）把游戏核限制在半数以内
            bool sameRing = AppConfig.GetBool("GameCoresSameRing", false);
            ulong exMask = CpuTopologyEx.GetGameMask(heavyCores, sameRing);
            if (exMask != 0) return exMask;
        }
        var (heavyMask, _) = CpuTopology.GetIsolationMasks(heavyCores);
        return heavyMask;
    }
    public static ulong GetBackgroundMask()
    {
        int heavyCores = CpuTopology.GetRecommendedGameCores();
        if (CpuTopologyEx.IsReady)
        {
            bool sameRing = AppConfig.GetBool("GameCoresSameRing", false);
            ulong exMask = CpuTopologyEx.GetBackgroundMask(heavyCores, sameRing);
            if (exMask != 0) return exMask;
        }
        var (_, bg) = CpuTopology.GetIsolationMasks(heavyCores);
        return bg;
    }
}
/// <summary>缓存感知（Memory/Cache Faults/sec）</summary>
public static class CacheAwareScheduler
{
    private static PerfCounterHelper _cacheFaultCounter;
    private static bool _initialized;
    private static bool _cachedSensitive;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static readonly object _lock = new();
    public static void Initialize()
    {
        _cacheFaultCounter = new PerfCounterHelper("Memory", "Cache Faults/sec");
        _initialized = _cacheFaultCounter.IsAvailable;
        AppLogger.Info(_initialized ? "✅ 缓存感知已启用" : "⚠️ 缓存计数器不可用，缓存感知已禁用");
    }
    public static bool IsCacheSensitive(int pid)
    {
        if (!_initialized) return false;
        lock (_lock)
        {
            if ((DateTime.Now - _cacheTime).TotalSeconds < Constants.CACHE_CHECK_CACHE_SECONDS) return _cachedSensitive;
            float faults = _cacheFaultCounter.GetValueCached();
            _cachedSensitive = faults > Constants.CACHE_MISS_SENSITIVE_THRESHOLD * Constants.CACHE_FAULT_SCALE;
            _cacheTime = DateTime.Now;
            return _cachedSensitive;
        }
    }
    public static ulong GetCacheOptimalMask(string processName, int pid)
    {
        if (!IsCacheSensitive(pid)) return AffinityResolver.GetDefaultMask();
        // 原实现“缓存敏感就只留 2 个物理核（CACHE_OPTIMAL_CORE_COUNT）”，
        // 对游戏/渲染类是灾难级降级（线程数远大于 2）。
        // 缓存敏感真正该做的是“限制在同一 L3 域内”（避免跨 CCD/环 访问），
        // 而不是把核数砍到 2 个。
        if (CpuTopologyEx.IsReady)
        {
            ulong sameDomain = CpuTopologyEx.GetGameMask(CpuTopology.GetRecommendedGameCores());
            if (sameDomain != 0) return sameDomain;
        }
        return AffinityResolver.GetHeavyLoadIsolationMask();
    }
    public static void Shutdown() => _cacheFaultCounter?.Dispose();
}
/// <summary>性能计数器封装</summary>
public sealed class PerfCounterHelper : IDisposable
{
    private System.Diagnostics.PerformanceCounter _counter;
    private float _cached;
    private DateTime _cacheTime = DateTime.MinValue;
    public bool IsAvailable { get; }
    public PerfCounterHelper(string category, string name)
    {
        try { _counter = new System.Diagnostics.PerformanceCounter(category, name, true); IsAvailable = true; }
        catch { IsAvailable = false; }
    }
    public float GetValueCached(double cacheSeconds = 1)
    {
        if (!IsAvailable) return 0;
        if ((DateTime.Now - _cacheTime).TotalSeconds < cacheSeconds) return _cached;
        try { _cached = _counter.NextValue(); _cacheTime = DateTime.Now; } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        return _cached;
    }
    public void Dispose() => _counter?.Dispose();
}
}
