// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Core/SchedulerCoordinator.cs
// 统一调度协调器（事件 + 2s快心跳 + 10s慢心跳）
// 首帧进程集合仅建基线不发布风暴；单帧事件上限50
// ============================================================
using System.Threading.Tasks;
using PCBooster.Execution.Jobs;
using PCBooster.Execution.Memory;
using PCBooster.Execution.Power;
using PCBooster.Execution.Preload;
using PCBooster.Execution.Rules;
using PCBooster.Execution.Work;
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;
using PCBooster.Intelligence;
namespace PCBooster.Core
{
/// <summary>统一调度协调器（双速心跳 + 事件驱动）</summary>
public static class SchedulerCoordinator
{
private static SafeTimer _fastTimer, _slowTimer;
// 卡死看门狗（本次 21:27–21:31 的 4 分钟空白就是没证据才难定位）
private static SafeTimer _stallTimer;
private static long _lastFastTickTicks;
private static long _cycleCount;
private static int _isFastExecuting, _isSlowExecuting;
private static int _lastRuleVersion;
    private static volatile int _fgPid;
    private static volatile string _fgName = "";
    private static SchedulerDegradationEngine.ScheduleLevel _level =
        SchedulerDegradationEngine.ScheduleLevel.Normal;
    private static HashSet<string> _lastProcessSet = new();
    // 前台推定源改为跨周期单例——原来每个心跳 new 一个，
    // “上次好值(0.6)”这一档永远走不到。
    private static readonly PresumedDataSource<int> _fgSource = new(
        () =>
        {
            int cached = _fgPid;
            if (cached > 0 && ProcessSafeHelper.IsAlive(cached)) return cached;
            int live = NativeApi.GetForegroundPid();
            if (live <= 0) throw new InvalidOperationException("无前台进程");
            return live;
        },
        () => 0,   // 不再拿 snapshot[0] 冒充前台（那可能是个完全无关的进程）
        0,
        pid => pid > 0 && ProcessSafeHelper.IsAlive(pid));
    private static volatile SchedulerContext _lastContext;
    private static SchedulerDegradationEngine.ScheduleLevel _pendingLevel;
    private static int _pendingLevelCount;
    private static string _lastFeedbackFgName = "";   // 前台切换命中检测
    private static bool _processSetWarmedUp; // 风暴抑制
    // 存(时间, 预测置信度)
    private static readonly Dictionary<string, (DateTime time, double confidence)> _pending = new();
    private static readonly object _pendingLock = new();
    // 预测登记冷却表（避免同一进程反复登记产生噪声/刷屏）
    private static readonly Dictionary<string, DateTime> _lastRegistered = new();
    public static long GetCycleCount() => _cycleCount;
    /// <summary>最近一次周期上下文（UI 诊断展示用）</summary>
    public static SchedulerContext GetLastContext() => _lastContext;
    /// <summary>当前降级等级（UI 诊断展示用）</summary>
    public static SchedulerDegradationEngine.ScheduleLevel GetScheduleLevel() => _level;
    public static void Initialize()
    {
        CpuTopology.Detect();
        PreloadRegistry.Load();
        // 改用异步心跳定时器（异常不再逃逸为未观察任务异常，
        // 由 SafeTimer 统一记录并上报模块健康度）
        _fastTimer = SafeTimer.Async("FastHeartbeat", OnFastTickAsync,
            TimeSpan.FromMilliseconds(Constants.FAST_HEARTBEAT_MS),
            TimeSpan.FromMilliseconds(Constants.FAST_HEARTBEAT_MS),
            ModuleRegistry.Names.SchedulerCoordinator);
        _slowTimer = SafeTimer.Async("SlowHeartbeat", OnSlowTickAsync,
            TimeSpan.FromMilliseconds(Constants.SLOW_HEARTBEAT_MS),
            TimeSpan.FromMilliseconds(Constants.SLOW_HEARTBEAT_MS),
            ModuleRegistry.Names.SchedulerCoordinator);
        // 看门狗独立计时，用来看“快心跳多久没跑了”
        _lastFastTickTicks = Environment.TickCount64;
        _stallTimer = new SafeTimer("StallWatchdog", CheckStall,
            TimeSpan.FromMilliseconds(Constants.STALL_WATCHDOG_INTERVAL_MS),
            TimeSpan.FromMilliseconds(Constants.STALL_WATCHDOG_INTERVAL_MS),
            ModuleRegistry.Names.SchedulerCoordinator);
        AppLogger.Info("✅ 调度器: 事件 + 2s/10s 双速心跳");
        // 原来 SystemWakeFromSleepEvent 没有任何订阅者，
        // 休眠/唤醒后电源方案、核心停泊、亲和性可能已被系统重置，
        // 而决策指纹还认为“已优化”，于是不会重新应用（唤醒后游戏不再优化）。
        EventBus.Subscribe<SystemWakeFromSleepEvent>(OnWakeFromSleep);
    }
    public static void Shutdown()
    {
        EventBus.Unsubscribe<SystemWakeFromSleepEvent>(OnWakeFromSleep);
        _fastTimer?.Dispose();
        _slowTimer?.Dispose();
        _stallTimer?.Dispose();
        ForegroundWatcher.Uninstall();
        WorkloadGuard.Reset();
    }

    /// <summary>卡死看门狗——快心跳被阻塞/整机卡顿超过阀值时留一条可定位的日志。</summary>
    private static void CheckStall()
    {
        try
        {
            long last = System.Threading.Interlocked.Read(ref _lastFastTickTicks);
            long gapMs = Environment.TickCount64 - last;
            if (gapMs < Constants.STALL_WARN_GAP_SECONDS * 1000L) return;
            var ctx = _lastContext;
            AppLogger.Warning($"⚠ 心跳中断 {gapMs / 1000}s（疑似系统级卡顿/黑屏）：" +
                $"场景={ctx?.Scene ?? "?"} 前台={ctx?.ForegroundProcessName ?? "?"} " +
                $"可用内存={ctx?.MemoryAvailable:F0}% CPU={ctx?.CpuUsage:F0}% 降级={_level}");
            // 记一次就重置，避免恢复后每 5s 反复报
            System.Threading.Interlocked.Exchange(ref _lastFastTickTicks, Environment.TickCount64);
        }
        catch (Exception ex) { AppLogger.Debug($"看门狗异常: {ex.Message}"); }
    }

    /// <summary>唤醒补偿：清空决策指纹并立即补跑一次慢心跳</summary>
    private static void OnWakeFromSleep(SystemWakeFromSleepEvent evt)
    {
        try
        {
            InstructionSetOptimizer.ResetState();
            AppLogger.Info($"💤 唤醒（间隔 {evt.GapMs / 1000}s）：已重置优化状态，重新接管");
            _ = Task.Run(OnSlowTickAsync);
        }
        catch (Exception ex) { AppLogger.Debug($"唤醒处理异常: {ex.Message}"); }
    }
    /// <summary>公开处理器，供 UI 线程钩子回调</summary>
    /// <summary>2 秒快心跳里的前台兜底（钩子事件丢失时仍能切换场景）</summary>
    private static void CheckForegroundFallback()
    {
        try
        {
            int live = NativeApi.GetForegroundPid();
            if (live <= 0 || live == _fgPid) return;
            string name = null;
            foreach (var p in NativeApi.GetSnapshotCached())
                if (p.Id == live) { name = p.ProcessName; break; }
            if (!string.IsNullOrEmpty(name)) HandleForeground(live, name);
        }
        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
    }

    public static void HandleForeground(int pid, string name)
    {
        _fgPid = pid; _fgName = name;
        // 前台切换即视为用户活动，立即从待命/休眠唤醒（原只能等 1s 轮询）
        PowerSaver.OnUserActivity();
        // 办公/浏览器前台活动时间戳（决定保护窗口）
        try { WorkloadGuard.OnForeground(pid, name); }
        catch (Exception ex) { AppLogger.Debug($"负载保护时间戳异常: {ex.Message}"); }
        try
        {
            // 切到前台立即解除限速/后台模式，避免“切回来还要卡一会儿”
            AutoJobManager.ReleaseJob(pid);
            var strategy = IntelligenceEngine.GetStrategy(name);
            if (strategy.BoostPriority)
                ProcessSafeHelper.SetPriority(pid, System.Diagnostics.ProcessPriorityClass.AboveNormal);
            // 办公程序保存/重算不排队——前台 Office/WPS 无条件提一档
            else if (WorkloadGuard.ShouldBoostForeground(name))
                ProcessSafeHelper.SetPriority(pid, System.Diagnostics.ProcessPriorityClass.AboveNormal);
            InstructionSetOptimizer.ApplyForForeground(pid, name, strategy);
        }
        catch (Exception ex) { AppLogger.Debug($"前台反应异常: {ex.Message}"); }
    }
    private static async Task OnFastTickAsync()
    {
        if (!Resilience.TryAcquire(ref _isFastExecuting)) return;
        try
        {
            System.Threading.Interlocked.Exchange(ref _lastFastTickTicks, Environment.TickCount64);
            _cycleCount++;
            NativeApi.RefreshMetricsInBackground();
            // 前台钩子失效时（远程会话/受限环境）用快心跳兜底发现切换
            CheckForegroundFallback();
            var context = CollectContext();
            if (StepSkipper.ShouldSkipAll(context, _level)) return;
            // 让所有 top-N 预测进入待确认表（原只有预加载候选，闭环无法自举）
            RegisterPendingPredictions(context);
            var tasks = new List<Task>();
            if (!StepSkipper.ShouldSkipMemory(context) &&
                ShouldRun(ModuleRegistry.Decisions.Memory,
                    l => l != SchedulerDegradationEngine.ScheduleLevel.Minimal))
                tasks.Add(Task.Run(() => ReclaimMemoryWithFeedback(context)));
            if (!StepSkipper.ShouldSkipPreload(context) &&
                _level == SchedulerDegradationEngine.ScheduleLevel.Normal &&
                ShouldRun(ModuleRegistry.Decisions.Preload,
                    l => l == SchedulerDegradationEngine.ScheduleLevel.Normal))
                tasks.Add(ExecutePreload(context));
            if (tasks.Count > 0) await Task.WhenAll(tasks);
            var launched = ExecuteFeedbackChecks(context);
            // 
            // ① 采集关闭时也必须更新场景——否则默认配置下游戏模式/电源锁定/核心隔离永不触发；
            // ② justLaunched 传“真实新启动的进程”。原实现把 top-10 预测当成启动事件，
            // 每 2 秒给预测进程刷一次启动计数，等于自我强化（越预测越“真”）。
            IntelligenceEngine.CollectData(NativeApi.GetSnapshotCached(), launched,
                context.ForegroundPid, AppConfig.EnableDataCollection);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"快心跳异常: {ex.Message}");
            ModuleHealthMonitor.RecordFailure(ModuleRegistry.Names.SchedulerCoordinator);
        }
        finally { Resilience.Release(ref _isFastExecuting); }
    }
    private static async Task OnSlowTickAsync()
    {
        if (!Resilience.TryAcquire(ref _isSlowExecuting)) return;
        try
        {
            var context = CollectContext();
            var (healthy, total) = ModuleHealthMonitor.GetHealthSummary();
            // 降级加滞回——恶化立即生效，恢复需连续 3 次（约 30s）稳定
            _level = StabilizeLevel(SchedulerDegradationEngine.Evaluate(context, healthy, total));
            var tasks = new List<Task> { Task.Run(() => PowerSaver.CheckWakeConditions()) };
            PreloadProtection.CheckExpired();
            // 探测编译工具链 / 办公与浏览器前台，刷新“免限速免修剪”窗口
            try { WorkloadGuard.Refresh(NativeApi.GetSnapshotCached(), context.ForegroundPid); }
            catch (Exception ex) { AppLogger.Debug($"负载保护刷新异常: {ex.Message}"); }
            int ruleVersion = ProcessRuleEngine.GetVersion();
            bool rulesChanged = ruleVersion != _lastRuleVersion;
            if (rulesChanged) _lastRuleVersion = ruleVersion;
            // 有启用规则时每轮检查新进程（原来只在规则版本变化时应用一次，
            // 启动之后才打开的程序永远不会命中规则）
            if ((rulesChanged || ProcessRuleEngine.HasActiveRules) &&
                ShouldRun(ModuleRegistry.Decisions.Rule,
                    l => l != SchedulerDegradationEngine.ScheduleLevel.Minimal))
            {
                var snapshot = NativeApi.GetSnapshotCached();
                tasks.Add(Task.Run(() => ApplyRulesWithFeedback(
                    snapshot, context, rulesChanged)));
            }
            if (AppConfig.EnableAutoJob &&
                ShouldRun(ModuleRegistry.Decisions.Auxiliary,
                    l => l != SchedulerDegradationEngine.ScheduleLevel.Minimal))
                tasks.Add(Task.Run(() => AutoJobManager.CheckAndApplyJobs(context)));
            tasks.Add(Task.Run(() => InstructionSetOptimizer.CheckAndApply(context)));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) { AppLogger.Error($"慢心跳异常: {ex.Message}"); }
        finally { Resilience.Release(ref _isSlowExecuting); }
    }
    private static SchedulerContext CollectContext()
    {
        var (cpu, mem) = CacheManager.GetSystemState();
        int fgPid = _fgSource.Get();
        string fgName = fgPid > 0 ? ProcessSafeHelper.GetProcessName(fgPid) : "";
        var context = new SchedulerContext
        {
            CpuUsage = cpu,
            MemoryAvailable = mem,
            ForegroundPid = fgPid,
            ForegroundProcessName = fgName,
            Predictions = IntelligenceEngine.PredictWithWakeup(10),
            Level = AdaptiveScheduler.GetCurrentLoadLevel(),
            PreloadPids = PreloadRegistry.Snapshot(),
            Scene = IntelligenceEngine.GetScene(),
            DataConfidence = _fgSource.Confidence
        };
        _lastContext = context;
        return context;
    }
    /// <summary>降级等级滞回——恶化立即生效，恢复需连续 3 次（约 30s）稳定，
    /// 避免预测/置信度边界抖动导致降级等级每 10 秒来回跳。</summary>
    private static SchedulerDegradationEngine.ScheduleLevel StabilizeLevel(
        SchedulerDegradationEngine.ScheduleLevel evaluated)
    {
        if (evaluated == _level) { _pendingLevelCount = 0; return _level; }
        if ((int)evaluated > (int)_level)   // 更差：立即生效
        {
            _pendingLevelCount = 0;
            AppLogger.Info($"🩺 降级: {_level} → {evaluated}");
            return evaluated;
        }
        if (evaluated == _pendingLevel) _pendingLevelCount++;
        else { _pendingLevel = evaluated; _pendingLevelCount = 1; }
        if (_pendingLevelCount < 3) return _level;
        AppLogger.Info($"🩺 恢复: {_level} → {evaluated}");
        _pendingLevelCount = 0;
        return evaluated;
    }

    private static bool ShouldRun(string decision,
        Func<SchedulerDegradationEngine.ScheduleLevel, bool> filter)
    {
        string module = ModuleRegistry.GetModuleForDecision(decision);
        if (!ModuleHealthMonitor.IsAvailable(module)) return false;
        // 模块休眠现在真正生效（原 IsSleeping 无任何消费方）
        if (PowerSaver.IsModuleSleeping(module)) return false;
        if (!filter(_level)) return false;
        if (!DecisionNecessityEvaluator.ShouldExecute(decision)) return false;
        return true;
    }
    /// <summary>内存回收回写决策效果（原只统计预加载）</summary>
    private static void ReclaimMemoryWithFeedback(SchedulerContext context)
    {
        // 功能被用户关闭时不写“无效”结果，避免污染决策效果统计
        if (!AppConfig.EnableMemoryReclaim) return;
        long freed = MemoryReclaimer.ReclaimMemory(context.PreloadPids, context.ForegroundPid);
        bool effective = freed > 0;
        DecisionEffectTracker.RecordResult(ModuleRegistry.Decisions.Memory, effective);
        DecisionNecessityEvaluator.OnExecuted(ModuleRegistry.Decisions.Memory, effective);
    }
    /// <summary>规则应用回写决策效果（原只统计预加载）</summary>
    private static void ApplyRulesWithFeedback(NativeApi.ProcessSnapshot[] snapshot,
        SchedulerContext context, bool recordEffect)
    {
        int applied = ProcessRuleEngine.ApplyAllRules(snapshot, context.CpuUsage, context.MemoryAvailable);
        // 常规轮次只在“确实应用了新进程”时回写效果，避免把“没有新进程”记成无效决策
        if (!recordEffect && applied == 0) return;
        bool effective = applied > 0;
        DecisionEffectTracker.RecordResult(ModuleRegistry.Decisions.Rule, effective);
        DecisionNecessityEvaluator.OnExecuted(ModuleRegistry.Decisions.Rule, effective);
    }
    /// <summary>预加载时记录真实预测置信度</summary>
    private static async Task ExecutePreload(SchedulerContext context)
    {
        var threshold = AppConfig.PreloadThreshold;
        // 纳入策略 PreloadPriority（原字段从未被使用）
        var candidates = context.Predictions
            .Where(p => p.confidence > threshold)
            .OrderByDescending(p => IntelligenceEngine.GetStrategy(p.process).PreloadPriority)
            .ThenByDescending(p => p.confidence)
            .Take(Constants.PRELOAD_MAX_CONCURRENT).ToList();
        var tasks = new List<Task>();
        foreach (var (process, confidence) in candidates)
        {
            int pid = NativeApi.FindPidByName(process);
            if (pid <= 0) continue;
            lock (_pendingLock) _pending[process] = (DateTime.Now, confidence);
            tasks.Add(PreloadAndTrack(pid, process, confidence));
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }
    private static async Task PreloadAndTrack(int pid, string process, double confidence)
    {
        // 三态结果——跳过（已在内存/去重/策略禁）不计入决策效果
        var outcome = await UnifiedPreloader.TryPreloadAsync(pid, process, confidence);
        if (outcome == PreloadOutcome.Skipped) return;
        bool success = outcome == PreloadOutcome.Preloaded;
        DecisionEffectTracker.RecordResult(ModuleRegistry.Decisions.Preload, success);
        DecisionNecessityEvaluator.OnExecuted(ModuleRegistry.Decisions.Preload, success);
    }
    /// <summary>预测登记——原实现只有“预加载候选”才进待确认表，
    /// 分数低于阈值时永远收不到命中，置信度不增长，形成死锁。现按 top-N 登记，带冷却。</summary>
    private static void RegisterPendingPredictions(SchedulerContext context)
    {
        if (context?.Predictions == null || context.Predictions.Count == 0) return;
        var now = DateTime.Now;
        // 冷却表自清理（上限保护）
        bool needTrim;
        lock (_pendingLock) needTrim = _lastRegistered.Count > 512;
        if (needTrim)
        {
            lock (_pendingLock)
            {
                var stale = new List<string>();
                foreach (var kv in _lastRegistered)
                    if ((now - kv.Value).TotalMinutes > 30) stale.Add(kv.Key);
                foreach (var k in stale) _lastRegistered.Remove(k);
            }
        }
        int registered = 0;
        foreach (var (process, confidence) in context.Predictions)
        {
            if (registered >= Constants.PENDING_REGISTER_TOP) break;
            if (string.IsNullOrEmpty(process)) continue;
            lock (_pendingLock)
            {
                if (_pending.Count >= Constants.PENDING_MAX_ENTRIES) break;
                if (_pending.ContainsKey(process)) { registered++; continue; }
                if (_lastRegistered.TryGetValue(process, out var at) &&
                    (now - at).TotalSeconds < Constants.PENDING_REGISTER_COOLDOWN_SECONDS)
                { registered++; continue; }
                _pending[process] = (now, confidence);
                _lastRegistered[process] = now;
            }
            registered++;
        }
    }
    /// <summary>取出待确认预测（名称匹配兼容原始名/规范化名）</summary>
    private static bool TryTakePending(string name, out double confidence)
    {
        confidence = 0;
        if (string.IsNullOrEmpty(name)) return false;
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(name, out var e))
            {
                confidence = e.confidence;
                _pending.Remove(name);
                return true;
            }
            var norm = StringHelper.NormalizeProcessName(name);
            if (norm != name && _pending.TryGetValue(norm, out e))
            {
                confidence = e.confidence;
                _pending.Remove(norm);
                return true;
            }
            return false;
        }
    }
    /// <summary>+ 风暴抑制: 命中反馈用真实置信度；首帧仅建基线；单帧≤50
    /// 返回本帧“真实新启动”的进程名，供学习模块记账（不再用预测结果冒充）</summary>
    private static List<string> ExecuteFeedbackChecks(SchedulerContext context)
    {
        var snapshot = NativeApi.GetSnapshotCached();
        var current = snapshot.Where(p => !string.IsNullOrEmpty(p.ProcessName))
                              .Select(p => p.ProcessName).ToHashSet();
        var launched = current.Except(_lastProcessSet)
            .Where(n => !ProcessMatcher.IsSystemProcess(n)).ToList();
        // ProcessExitedEvent 此前定义后从未发布
        var exited = _lastProcessSet.Except(current)
            .Where(n => !ProcessMatcher.IsSystemProcess(n)).ToList();
        _lastProcessSet = current;
        if (!_processSetWarmedUp) { _processSetWarmedUp = true; launched.Clear(); exited.Clear(); }
        // 同帧事件批量投递（原来每个事件各起一个线程池任务）
        var exitedBatch = new List<ProcessExitedEvent>();
        foreach (var gone in exited.Take(Constants.EXIT_EVENT_LIMIT))
            exitedBatch.Add(new ProcessExitedEvent(0, gone, DateTime.Now, EventBus.NextSeq()));
        if (exitedBatch.Count > 0) EventBus.PublishBatch(exitedBatch);

        var launchBatch = new List<ProcessLaunchedEvent>();
        foreach (var name in launched.Take(50))
        {
            // 统一用 TryTakePending（兼容大小写/后缀差异）
            bool wasPredicted = TryTakePending(name, out double conf);
            if (wasPredicted) IntelligenceEngine.OnFeedback(name, true, conf); // 
            launchBatch.Add(new ProcessLaunchedEvent(
                NativeApi.FindPidByName(name), name, DateTime.Now, EventBus.NextSeq()));
        }
        if (launchBatch.Count > 0) EventBus.PublishBatch(launchBatch);
        // 前台切换也作为命中信号——长驻程序（微信/浏览器）永远不会产生“启动”事件
        if (!string.IsNullOrEmpty(context.ForegroundProcessName) &&
            !string.Equals(context.ForegroundProcessName, _lastFeedbackFgName, StringComparison.OrdinalIgnoreCase))
        {
            _lastFeedbackFgName = context.ForegroundProcessName;
            if (TryTakePending(context.ForegroundProcessName, out double fgConf))
                IntelligenceEngine.OnFeedback(context.ForegroundProcessName, true, fgConf);
        }
        List<string> expired;
        lock (_pendingLock)
        {
            expired = _pending.Where(kv => (DateTime.Now - kv.Value.time).TotalSeconds >
                Constants.PREDICTION_WINDOW_SECONDS).Select(kv => kv.Key).ToList();
            foreach (var n in expired) _pending.Remove(n);
        }
        foreach (var n in expired)
            // 预测已过期但进程一直在运行 → 不是“抛空”，
            // 只是没发生启动事件；扣分会系统性压低长驻程序（浏览器/微信）的置信度。
            if (NativeApi.FindPidByName(n) <= 0)
                IntelligenceEngine.OnFeedback(n, false, 0);
        PreloadRegistry.Sync(snapshot.Select(p => (p.Id, p.ProcessName)));
        return launched;
    }
}
}
