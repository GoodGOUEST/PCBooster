// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Profiling/PowerTuning.cs
// 电源调优套件（FrequencyManager + WorkingSetLock + InstructionSetOptimizer）
// ============================================================
using PCBooster.Core;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;
namespace PCBooster.Infrastructure.Profiling
{
/// <summary>CPU 频率锁定/恢复（绑定场景 + 安全网持有引用）</summary>
public static class FrequencyManager
{
    private static readonly object _lock = new();
    private static bool _isLocked;
    private static string _lockedBy = "";
    private static SafeTimer _safetyTimer;
    public static void Initialize()
    {
        EventBus.Subscribe<SceneChangedEvent>(OnSceneChanged);
        _safetyTimer = new SafeTimer("FrequencySafety", SafetyCheck,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
            ModuleRegistry.Names.PowerSaver);
    }
    private static void OnSceneChanged(SceneChangedEvent evt)
    {
        if (evt.NewScene == Constants.SCENE_GAMING && evt.OldScene != Constants.SCENE_GAMING)
        {
            // 回到游戏 → 取消待执行的恢复（去抖的另一半）
            CancelPendingRestore();
            // 黑屏修复: 不在这里直接锁频。全屏游戏切显示模式/短暂失焦时
            // 场景会在 gaming ↔ gaming-bg 夹秒级抖动，抖动中锁频 = 不停重激活电源方案。
            // 改为“等场景稳定 FREQ_LOCK_STABLE_SECONDS 后再锁”。
            MarkGamingEntered();
            RequestLock("GameScene", 0);
        }
        // 「游戏中途跳闪后黑屏」：离开游戏不再立刻恢复电源方案。
        // 如实测日志，全屏游戏切换显示模式/短暂掉前台时场景会在 gaming ↔ gaming-bg 抖动，
        // 旧版每次抖动都锁频/解锁一次（2 秒内反复切 powercfg 方案 + 写 5 个电源项），
        // 进而打断 GPU 提交节奏（同刻 nvlddmkm 153），表现就是闪屏/黑屏。
        // 现在改为延迟确认：宽限期内回到游戏就什么都不做。
        if (evt.OldScene == Constants.SCENE_GAMING && evt.NewScene != Constants.SCENE_GAMING)
            ScheduleRestore();

        // 影音场景只关核心停泊（解码线程不被停泊拖抖），不切高性能方案。
        if (evt.NewScene == Constants.SCENE_VIDEO)
        {
            NativeApi.SetCoreParkingPercent(Constants.CORE_PARKING_ACTIVE);
            _videoParking = true;
        }
        else if (evt.OldScene == Constants.SCENE_VIDEO && _videoParking && !_isLocked)
        {
            NativeApi.RestoreParkingBaseline();
            _videoParking = false;
        }
    }

    private static bool _videoParking;
    // 进入游戏的时间点、全局转换时间、被节流的次数
    private static DateTime _gamingSince = DateTime.MinValue;
    private static DateTime _lastTransitionAt = DateTime.MinValue;
    private static long _suppressedTransitions;
    private static System.Threading.Timer _pendingLock;

    private static void MarkGamingEntered()
    {
        lock (_lock) _gamingSince = DateTime.Now;
    }

    /// <summary>场景稳定后才真正锁频（防全屏切换夹秒级抖动导致的电源振荡/黑屏）</summary>
    public static void RequestLock(string lockedBy, int pid)
    {
        lock (_lock)
        {
            if (_isLocked) return;
            double elapsed = (DateTime.Now - _gamingSince).TotalSeconds;
            double need = Constants.FREQ_LOCK_STABLE_SECONDS;
            if (elapsed >= need) { LockMaxFrequency(lockedBy, pid); return; }
            _pendingLock?.Dispose();
            int delayMs = (int)Math.Max(300, (need - elapsed) * 1000);
            _pendingLock = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    if (_isLocked) return;
                    if (IntelligenceEngine.GetScene() != Constants.SCENE_GAMING) return;
                    if ((DateTime.Now - _gamingSince).TotalSeconds < need) return;
                    LockMaxFrequency(lockedBy, pid);
                }
            }, null, delayMs, System.Threading.Timeout.Infinite);
            AppLogger.Debug($"⚡ 场景抖动：{delayMs}ms 后再确认锁频");
        }
    }
    public static void LockMaxFrequency(string lockedBy = "", int pid = 0)
    {
        lock (_lock)
        {
            if (_isLocked) return;
            // 黑屏修复: 全局转换节流。实测日志 21:13:00→21:13:09 九秒内做了
            // “恢复→锁频→恢复→锁频”两轮（每轮写 5 项电源 + 方案重激活），
            // 活动画面上就是一黑一闪。这里把任何方向的转换最小间隔卡在 15s。
            if (_lastTransitionAt != DateTime.MinValue &&
                (DateTime.Now - _lastTransitionAt).TotalSeconds < Constants.FREQ_TRANSITION_MIN_INTERVAL_SECONDS)
            {
                _suppressedTransitions++;
                AppLogger.Debug($"⚡ 锁频被节流（距上次电源转换不足 {Constants.FREQ_TRANSITION_MIN_INTERVAL_SECONDS}s，累计 {_suppressedTransitions} 次）");
                return;
            }
            // 先确认真的切到了高性能方案；
            // 旧版不看返回值就置 _isLocked，导致“界面已锁定、实际没切”。
            if (!NativeApi.ApplyHighPerformance())
            {
                _lastLockError = "电源方案切换失败（可能被组策略/OEM 锁定）";
                AppLogger.Warning("⚡ 频率锁定失败: " + _lastLockError);
                return;
            }
            NativeApi.SetCoreParkingPercent(Constants.CORE_PARKING_ACTIVE);
            // 多核低频平台补上睿频/升频设置（接通电源时生效）
            NativeApi.ApplyGamingPowerProfile();
            _isLocked = true;
            _lastLockError = "";
            _lockedBy = lockedBy;
            _lockedPid = pid;
            _lastTransitionAt = DateTime.Now;
            AppLogger.Info($"⚡ 频率锁定: {lockedBy}");
        }
    }

    private static string _lastLockError = "";
    /// <summary>最近一次锁频失败原因（UI 直接展示）</summary>
    public static string LastLockError { get { lock (_lock) return _lastLockError; } }

    // 离开游戏的去抖（宽限期内回到游戏则不恢复电源方案）
    private static System.Threading.Timer _restoreTimer;

    private static void ScheduleRestore()
    {
        lock (_lock)
        {
            if (!_isLocked) return;                      // 本来就没锁，无需安排
            _restoreTimer?.Dispose();
            _restoreTimer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    if (!_isLocked) return;
                    // 到点再确认一次：宽限期内已回到游戏就保留锁定
                    if (IntelligenceEngine.GetScene() == Constants.SCENE_GAMING) return;
                    RestoreAutoFrequency();
                }
            }, null, TimeSpan.FromSeconds(Constants.GAMING_EXIT_POWER_RESTORE_DELAY_SECONDS),
               System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private static void CancelPendingRestore()
    {
        lock (_lock) { _restoreTimer?.Dispose(); _restoreTimer = null; }
    }

    public static void RestoreAutoFrequency()
    {
        lock (_lock)
        {
            if (!_isLocked) return;
            // 同方向节流——恢复也是 10+ 项电源写 + 方案切换，不能与刚发生的锁频背靠背
            if (_lastTransitionAt != DateTime.MinValue &&
                (DateTime.Now - _lastTransitionAt).TotalSeconds < Constants.FREQ_TRANSITION_MIN_INTERVAL_SECONDS)
            {
                _suppressedTransitions++;
                AppLogger.Debug($"⚡ 恢复被节流（距上次电源转换不足 {Constants.FREQ_TRANSITION_MIN_INTERVAL_SECONDS}s），稍后重试");
                ScheduleRestore();
                return;
            }
            // 这里以前只 RestoreOriginalScheme，
            // 游戏电源档（睿频/处理器状态）留在方案里没还原。
            NativeApi.RestorePowerSettings();
            _isLocked = false; _lockedBy = "";
            _lastTransitionAt = DateTime.Now;
            AppLogger.Info("⚡ 频率恢复自动");
        }
    }
    private static int _lockedPid;

    /// <summary>安全网：只在“已不在游戏场景”或“被锁进程退出”时兜底恢复</summary>
    private static void SafetyCheck()
    {
        lock (_lock)
        {
            if (!_isLocked) return;
            // 原来是“锁定超过 10 分钟就自动恢复”——
            // 长时间打游戏会在中途悄悄掉回平衡方案（页面还显示已锁定），
            // 而且决策指纹不变、不会重新锁。真实表现就是“玩十几分钟后开始掉帧”。
            // 现在只看场景与被锁进程是否还活着，与时长无关。
            bool sceneGaming = IntelligenceEngine.GetScene() == Constants.SCENE_GAMING;
            bool pidAlive = _lockedPid <= 0 || ProcessAlive(_lockedPid);
            if (!sceneGaming || !pidAlive)
            {
                AppLogger.Info($"⚡ 安全网恢复（场景={sceneGaming}, 进程存活={pidAlive}）");
                RestoreAutoFrequency();
            }
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
    public static bool IsLocked { get { lock (_lock) return _isLocked; } }
}
/// <summary>工作集保护（权限检查 + ARM64 禁用）</summary>
public static class WorkingSetLock
{
    private static bool _isEnabled;
    private static bool _checked;
    private static readonly HashSet<int> _locked = new();
    private static readonly object _lockObject = new();
    public static void Initialize()
    {
        if (_checked) return;
        _checked = true;
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        { _isEnabled = false; return; }
        _isEnabled = AppEnvironment.IsElevated;
        AppLogger.Info(_isEnabled ? "✅ 工作集保护已启用" : "⚠️ 权限不足，工作集保护已禁用");
    }
    public static bool EnableForProcess(int pid)
    {
        if (!_isEnabled) return false;
        // 卡死修复: 原来没有任何去重，前台事件每次都会再调一次
        // SetProcessWorkingSetSizeEx（把工作集下限拉到巨大值）。
        // 一场游戏里会被重复调用上百次，每次都把内存顶向“锁死”状态，
        // 机器在前台卡/切场景时就会主动触发全局换页风暴。
        lock (_lockObject)
        {
            if (_locked.Contains(pid)) return true;
            if (_locked.Count > 64)
            {
                var dead = _locked.Where(p => !NativeApi.IsPidAliveCached(p)).ToList();
                foreach (var p in dead) _locked.Remove(p);
            }
        }
        if (ProcessSafeHelper.GetWorkingSet(pid) <= Constants.LARGE_PAGE_WORKING_SET_THRESHOLD) return false;
        bool ok = NativeApi.LockProcessMemory(pid);
        if (ok) lock (_lockObject) _locked.Add(pid);
        return ok;
    }
    public static bool IsEnabled => _isEnabled;
    /// <summary>释放所有工作集下限（关闭开关 / 一键还原 / 退出时调用）</summary>
    public static int ReleaseAllFloors()
    {
        int n = 0;
        int[] pids;
        lock (_lockObject) { pids = new int[_locked.Count]; _locked.CopyTo(pids); _locked.Clear(); }
        foreach (int pid in pids) if (NativeApi.ReleaseWorkingSetFloor(pid)) n++;
        if (n > 0) AppLogger.Info($"工作集下限已释放: {n} 个进程");
        return n;
    }
}
/// <summary>指令集优化（独立于提权决策）</summary>
public static class InstructionSetOptimizer
{
    // 决策指纹（PID + 场景 + 策略标志）。原来只比较前台 PID：
    // 游戏先启动、几秒后才被识别为 gaming 时 PID 没变，整个 CheckAndApply
    // 直接 return，导致“场景已是游戏中，但隔离/锁频永远不生效”。
    private static string _lastOptimizedKey = "";
    private static long _lastIsolationRetryTick;
    private const int IsolationRetryMs = 20000;
    // 失败后指数退避（20s → 40s → … 封顶 5 分钟），避免在“一个可隔离进程都没有”的机器上反复扫快照
    private static int _isolateRetryDelayMs = IsolationRetryMs;
    private static string _isolationNote = "";
    // 与其它核心调度工具互斥（借鉴 FlexCore 的“两者不能同时接管调度”约定）
    private static readonly string[] _rivalHints =
    {
        "processlasso", "flexcore", "parkcontrol", "quickcpu", "cpucores", "coreprio"
    };
    private static DateTime _rivalCheckedAt = DateTime.MinValue;
    private static string _rivalName = "";
    private static bool _rivalLogged;
    /// <summary>检测到另一个核心调度工具在跑时，我们让位（不抢亲和性），避免两家互改同一个掩码</summary>
    private static bool RivalToolRunning()
    {
        if ((DateTime.Now - _rivalCheckedAt).TotalSeconds < 60)
            return _rivalName.Length > 0;
        _rivalCheckedAt = DateTime.Now;
        _rivalName = "";
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName.ToLowerInvariant();
                    if (_rivalHints.Any(h => n.Contains(h))) { _rivalName = p.ProcessName; break; }
                }
                catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                finally { p.Dispose(); }
            }
        }
        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        if (_rivalName.Length > 0 && !_rivalLogged)
        {
            _rivalLogged = true;
            AppLogger.Warning($"⚠ 检测到其它核心调度工具（{_rivalName}）：为避免互抢亲和性，核心绑定/后台核心罩让位（锁频仍生效）");
        }
        return _rivalName.Length > 0;
    }
    // 亲和性去重表 + 后台核心罩扫描节流
    private static readonly Dictionary<int, (ulong mask, DateTime at)> _lastAffinity = new();
    private static readonly object _affinityLock = new();
    private static DateTime _lastScanAt = DateTime.MinValue;
    // 后台核心罩跟踪（原 AffinityResolver.GetBackgroundMask 从未被调用，隔离只有单向）
    private static readonly HashSet<int> _bgIsolated = new();
    private static readonly object _bgLock = new();
    /// <summary>供 UI 展示“后台核心罩是否生效”</summary>
    public static int IsolatedCount { get { lock (_bgLock) return _bgIsolated.Count; } }
    /// <summary>供 UI 展示的可读状态（为什么生效/为什么没生效）</summary>
    public static string GetIsolationStatus()
    {
        int count = IsolatedCount;
        if (RivalToolRunning()) return $"让位 · 检测到 {_rivalName}（避免互抢亲和性）";
        if (count > 0)
        {
            int phys = CpuTopology.PhysicalCoreCount;
            int bgCores = Math.Max(0, phys - CpuTopology.GetRecommendedGameCores());
            int bgThreads = bgCores * (CpuTopology.HasSMT ? 2 : 1);
            return $"生效 · {count} 个后台进程 · 后台罩 {bgCores} 核/{bgThreads} 线程 · L3域 {CpuTopologyEx.L3DomainCount}";
        }
        if (!AppConfig.GetBool("EnableInstructionSetScheduling", true)) return "设置里已关闭";
        if (CpuTopology.PhysicalCoreCount <= 0) return "处理器拓扑检测中";
        if (AffinityResolver.GetBackgroundMask() == 0) return "物理核不足（单核），无法隔离";
        if (!Constants.ISOLATION_SCENES.Contains(IntelligenceEngine.GetScene())) return "当前场景不需要";
        string note;
        lock (_bgLock) note = _isolationNote;
        return string.IsNullOrEmpty(note) ? "等待应用（自动重试）" : note;
    }
    private static void Note(string text)
    {
        lock (_bgLock) _isolationNote = text;
    }

    /// <summary>一键还原时清空决策状态，
    /// 否则“已优化”指纹会让引擎以为策略还在，不再重新应用。</summary>
    public static void ResetState()
    {
        lock (_bgLock)
        {
            _bgIsolated.Clear();
            _isolationNote = "";
        }
        _lastOptimizedKey = "";
        _lastIsolationRetryTick = 0;
        _isolateRetryDelayMs = IsolationRetryMs;
    }
    private static string BuildDecisionKey(int pid, string scene, Strategy strategy) =>
        $"{pid}|{scene}|{strategy?.CoreIsolation}|{strategy?.LockFrequency}|" +
        $"{strategy?.EnableWorkingSetLock}|{strategy?.CacheAware}";
    /// <summary>亲和性写入去重——同一进程同一掩码 30s 内不重复写。
    /// 每次 SetProcessAffinityMask 都会让目标进程（尤其是正在提交帧的游戏）抖一下，
    /// 而前台事件是每 200ms 就可能来一次的。</summary>
    private static bool ApplyAffinityOnce(int pid, ulong mask)
    {
        if (mask == 0) return false;
        var now = DateTime.Now;
        lock (_affinityLock)
        {
            if (_lastAffinity.TryGetValue(pid, out var e) && e.mask == mask &&
                (now - e.at).TotalSeconds < Constants.AFFINITY_REAPPLY_MIN_INTERVAL_SECONDS) return false;
            if (_lastAffinity.Count > 512)
            {
                var dead = _lastAffinity.Keys.Where(p => !NativeApi.IsPidAliveCached(p)).ToList();
                foreach (var p in dead) _lastAffinity.Remove(p);
            }
            _lastAffinity[pid] = (mask, now);
        }
        return ProcessSafeHelper.SetAffinity(pid, mask);
    }
    public static bool CheckAndApply(SchedulerContext context)
    {
        if (context == null || context.ForegroundPid <= 0) return false;
        if (!AppConfig.GetBool("EnableInstructionSetScheduling", true))
        {
            Note("设置里已关闭");
            return false;
        }
        string name = context.ForegroundProcessName;
        if (string.IsNullOrEmpty(name)) return false;
        var strategy = IntelligenceEngine.GetStrategy(name);
        if (strategy == null) return false;

        // 行为门控: 仅重负载场景（游戏/设计/开发）启用核心隔离
        bool needsIsolation = Constants.ISOLATION_SCENES.Contains(context.Scene);
        string key = BuildDecisionKey(context.ForegroundPid, context.Scene, strategy);
        // 隔离可能因为“拓扑未就绪 / 当时没有可隔离的后台进程”而落空；
        // 只要场景需要且还没生效，就按 20 秒节流重试，而不是等下一次切换前台。
        bool retryIsolation = needsIsolation && IsolatedCount == 0 &&
            Environment.TickCount64 - _lastIsolationRetryTick > _isolateRetryDelayMs;
        if (key == _lastOptimizedKey && !retryIsolation) return false;
        bool changed = false;

        if (strategy.CoreIsolation && CpuTopology.HasSMT && needsIsolation && !RivalToolRunning())
            changed |= ApplyAffinityOnce(context.ForegroundPid, AffinityResolver.GetHeavyLoadIsolationMask());
            
        if (strategy.LockFrequency && AppConfig.GetBool("EnableFrequencyLock", true))
            { FrequencyManager.LockMaxFrequency(name, context.ForegroundPid); changed = true; }
        if (strategy.EnableWorkingSetLock && AppConfig.EnableWorkingSetLock)
            changed |= WorkingSetLock.EnableForProcess(context.ForegroundPid);
        if (strategy.CacheAware && CacheAwareScheduler.IsCacheSensitive(context.ForegroundPid))
            changed |= ApplyAffinityOnce(context.ForegroundPid, CacheAwareScheduler.GetCacheOptimalMask(name, context.ForegroundPid));
            
        // 重载场景下同时把后台进程压到后台核（离开场景时恢复）
        ApplyBackgroundIsolation(needsIsolation, context.ForegroundPid);
        _lastOptimizedKey = key;
        if (changed) AppLogger.Info($"📐 指令集优化: {name} (场景:{context.Scene})");
        return changed;
    }
    /// <summary>事件驱动入口（前台钩子调用）</summary>
    public static bool ApplyForForeground(int pid, string name, Strategy strategy)
    {
        if (string.IsNullOrEmpty(name) || strategy == null) return false;
        
        // 行为门控: 读取当前场景，判断是否属于重负载隔离集合
        bool needsIsolation = Constants.ISOLATION_SCENES.Contains(IntelligenceEngine.GetScene());
        bool changed = false;
        
        if (strategy.CoreIsolation && CpuTopology.HasSMT && needsIsolation && !RivalToolRunning())
            changed |= ApplyAffinityOnce(pid, AffinityResolver.GetHeavyLoadIsolationMask());
            
        if (strategy.LockFrequency && AppConfig.GetBool("EnableFrequencyLock", true))
        // 走“稳定确认”路径，避免前台事件夹秒级抖动时反复重激活电源方案
        FrequencyManager.RequestLock(name, pid);
        if (strategy.EnableWorkingSetLock && AppConfig.EnableWorkingSetLock)
            changed |= WorkingSetLock.EnableForProcess(pid);
            
        ApplyBackgroundIsolation(needsIsolation, pid);
        _lastOptimizedKey = BuildDecisionKey(pid, IntelligenceEngine.GetScene(), strategy);
        if (changed) AppLogger.Info($"📐 指令集优化(事件): {name}");
        return changed;
    }
    /// <summary>场景进入时把非前台进程压到后台核；离开时恢复默认掩码</summary>
    private static void ApplyBackgroundIsolation(bool isolate, int foregroundPid)
    {
        if (!AppConfig.GetBool("EnableInstructionSetScheduling", true))
        {
            Note("设置里已关闭");
            return;
        }
        // 有其它调度工具在跑时，后台核心罩也让位
        if (isolate && RivalToolRunning())
        {
            Note($"让位 · 检测到 {_rivalName}");
            return;
        }
        // 定期剔除已退出 PID，避免集合与“隔离生效”状态长期虚高
        lock (_bgLock)
        {
            if (_bgIsolated.Count > 64)
            {
                var dead = _bgIsolated.Where(pid => !NativeApi.IsPidAliveCached(pid)).ToList();
                foreach (var pid in dead) _bgIsolated.Remove(pid);
            }
        }
        if (!isolate)
        {
            List<int> restore;
            lock (_bgLock)
            {
                if (_bgIsolated.Count == 0) return;
                restore = _bgIsolated.ToList();
                _bgIsolated.Clear();
            }
            ulong def = AffinityResolver.GetDefaultMask();
            ulong bgMaskNow = AffinityResolver.GetBackgroundMask();
            int n = 0;
            foreach (int pid in restore)
            {
                // 只还原仍处于“后台罩”的进程；
                // 若用户规则/其他工具已改过亲和性，不要用默认掩码覆盖它。
                if (!ProcessSafeHelper.IsAlive(pid)) continue;
                if (bgMaskNow != 0 && ProcessSafeHelper.GetAffinity(pid) != bgMaskNow) continue;
                if (ProcessSafeHelper.SetAffinity(pid, def)) n++;
            }
            if (n > 0) AppLogger.Info($"📐 后台核心罩已恢复: {n} 个进程");
            _isolateRetryDelayMs = IsolationRetryMs;
            Note("当前场景不需要");
            return;
        }
        ulong bgMask = AffinityResolver.GetBackgroundMask();
        if (bgMask == 0)
        {
            Note("处理器拓扑未就绪");
            return;   // 拓扑尚未就绪
        }
        // 扫描节流——前台事件很密，而这里要遍历整个快照并对多个进程写亲和性。
        // 实测日志 7 秒内刷了 3 次“后台核心罩生效”。
        if ((DateTime.Now - _lastScanAt).TotalMilliseconds < Constants.BACKGROUND_ISOLATION_MIN_SCAN_INTERVAL_MS) return;
        _lastScanAt = DateTime.Now;
        _lastIsolationRetryTick = Environment.TickCount64;
        int done = 0;
        int scanned = 0;
        foreach (var p in NativeApi.GetSnapshotCached())
        {
            if (done >= Constants.BACKGROUND_ISOLATION_MAX_PER_CYCLE) break;
            // 失败/跳过路径不计数，原实现会一直扫完整个快照（2000+ 进程）
            if (++scanned > Constants.BACKGROUND_ISOLATION_MAX_PER_CYCLE * 8) break;
            if (p.Id == foregroundPid || p.Id <= 0) continue;
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
            if (ProcessMatcher.IsProtected(p.ProcessName)) continue;   // 红线: 不动反作弊进程
            // 游戏进程的亲和性由“前台路径”管。旧逻辑里切出游戏（去 Discord/浏览器）时，
            // 游戏会被当成普通后台进程压到后台核，切回来再重新压回重载核 —— 每次 Alt-Tab 都抖一下。
            if (ProcessMatcher.IsGame(p.ProcessName)) continue;
            lock (_bgLock) { if (_bgIsolated.Contains(p.Id)) continue; }
            if (ProcessSafeHelper.SetAffinity(p.Id, bgMask))
            {
                lock (_bgLock) _bgIsolated.Add(p.Id);
                done++;
            }
        }
        // 全部失败（多数是权限不足或候选进程都受保护）时，把一个可读原因留给 UI
        if (done > 0)
        {
            _isolateRetryDelayMs = IsolationRetryMs;
            Note("");
            AppLogger.Info($"📐 后台核心罩生效: {done} 个进程");
        }
        else
        {
            _isolateRetryDelayMs = Math.Min(_isolateRetryDelayMs * 2, 300000);
            Note("没有可隔离的后台进程（或权限不足）");
        }
    }
}
}
