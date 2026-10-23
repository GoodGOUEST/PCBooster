// ============================================================
// PC Booster v3.6.8
// Intelligence/IntelligenceEngine.cs
// 3.6.7: 唤醒/闲时门控具名化 / 4参 Classify 透传 / OnFeedback 发布 PredictionResultEvent
// ============================================================
using System.Threading.Tasks;
using PCBooster.Infrastructure.Common;
using NativeApiSnapshot = PCBooster.Infrastructure.Native.NativeApi.ProcessSnapshot;
namespace PCBooster.Intelligence
{
/// <summary>统一智能引擎</summary>
public static class IntelligenceEngine
{
private static readonly DataCollector _collector = new();
private static readonly ModelEngine _models = new();
private static readonly FeedbackLoop _feedback = new();
private static readonly LearningScheduler _learner = new();
private static PersistenceManager<LearningData> _persistence;
private static SafeTimer _idleTimer, _flushTimer;
private static int _totalSamples;
private static bool _savedOnce;
    private static List<(string process, double confidence)> _predCache = new();
    private static DateTime _predCacheTime = DateTime.MinValue;
    private static int _predCacheTopN = -1;   // ★ 3.6.8 修复: 缓存需按 topN 区分
    private static readonly object _predLock = new();
    public static void Initialize()
    {
        _persistence = new PersistenceManager<LearningData>(Constants.FILE_LEARNING);
        var loaded = _persistence.Load();
        if (loaded != null) { _collector.Load(loaded); _models.Load(loaded); _feedback.Load(loaded); }
        // ★ 3.6.7: 异步回调交给 SafeTimer 托管（原 fire-and-forget 会丢失异常与健康上报）
        _idleTimer = SafeTimer.Async("IdleLearning", TryIdleLearning,
            TimeSpan.FromSeconds(Constants.IDLE_CHECK_INTERVAL_SECONDS),
            TimeSpan.FromSeconds(Constants.IDLE_CHECK_INTERVAL_SECONDS),
            ModuleRegistry.Names.IntelligenceEngine);
        _flushTimer = new SafeTimer("LearningFlush", () => Save(),
            TimeSpan.FromSeconds(Constants.FLUSH_INTERVAL_SECONDS),
            TimeSpan.FromSeconds(Constants.FLUSH_INTERVAL_SECONDS),
            ModuleRegistry.Names.IntelligenceEngine);
        AppLogger.Info($"🧠 智能引擎已初始化: {GetStats()}");
    }
    public static void CollectData(NativeApiSnapshot[] procs, List<string> justLaunched, int fgPid,
        bool learn)
    {
        try
        {
            _collector.Collect(procs, justLaunched, fgPid, learn);
            if (learn)
            {
                // ★ 3.6.8 修复: 实时学习序列（原来只在闲时每 5 分钟学一条）
                _models.LearnSequence(_collector);
                System.Threading.Interlocked.Add(ref _totalSamples, procs?.Length ?? 0);
            }
        }
        catch (Exception ex) { AppLogger.Debug($"采集异常: {ex.Message}"); }
    }
    /// <summary>★ 3.6.8: 有效采集分钟数（冷启动 / UI 进度用）</summary>
    public static int GetCollectedMinutes() => _collector.CollectedMinutes;
    public static List<(string process, double confidence)> Predict(int topN = 10)
    {
        lock (_predLock)
        {
            if ((DateTime.Now - _predCacheTime).TotalSeconds < Constants.PREDICTION_CACHE_SECONDS
                && _predCache.Count > 0 && _predCacheTopN == topN) return _predCache;
        }
        List<(string, double)> result;
        try { result = _models.Predict(_collector, topN); }
        catch (Exception ex) { AppLogger.Debug($"预测异常: {ex.Message}"); result = new(); }
        lock (_predLock) { _predCache = result; _predCacheTime = DateTime.Now; _predCacheTopN = topN; }
        return result;
    }
    public static List<(string process, double confidence)> PredictWithWakeup(int topN = 10)
    {
        var predictions = Predict(topN);
        foreach (var (process, confidence) in predictions)
        {
            if (confidence > Constants.WAKE_CONFIDENCE_THRESHOLD) // ★ 具名
            {
                Execution.Power.PowerSaver.WakeModule(ModuleRegistry.Names.UnifiedPreloader);
                break;
            }
        }
        return predictions;
    }
    public static ProcessClass Classify(string processName)
    {
        try { return _models.Classify(_collector, processName); }
        catch { return HeuristicFallback(processName); }
    }
    /// <summary>★ 4参活体分类透传</summary>
    public static ProcessClass Classify(string processName, long liveWs, double liveCpu, bool isForeground)
    {
        try { return _models.Classify(_collector, processName, liveWs, liveCpu, isForeground); }
        catch { return HeuristicFallback(processName); }
    }
    private static ProcessClass HeuristicFallback(string processName)
    {
        if (ProcessMatcher.IsSystemProcess(processName)) return ProcessClass.System;
        if (ProcessMatcher.IsGame(processName)) return ProcessClass.HeavyCompute;
        if (ProcessMatcher.IsBrowser(processName)) return ProcessClass.HeavyMemory;
        return ProcessClass.Unknown;
    }
    // ★ 3.6.8 性能: 策略缓存（Classify 要做滑窗统计，而慢心跳里同一进程会被反复询问）
    private static readonly Dictionary<string, (Strategy s, DateTime at)> _strategyCache = new();
    private static readonly object _strategyLock = new();
    public static Strategy GetStrategy(string processName)
    {
        string key = (processName ?? string.Empty).ToLowerInvariant();
        lock (_strategyLock)
        {
            if (_strategyCache.TryGetValue(key, out var hit) &&
                (DateTime.Now - hit.at).TotalSeconds < Constants.STRATEGY_CACHE_SECONDS)
                return hit.s;
        }
        var s = _models.GetStrategy(Classify(processName));
        lock (_strategyLock)
        {
            if (_strategyCache.Count > 512) _strategyCache.Clear();
            _strategyCache[key] = (s, DateTime.Now);
        }
        return s;
    }
    public static string GetClassName(ProcessClass cls) => cls switch
    {
        ProcessClass.HeavyCompute => "🔥 大型重载",
        ProcessClass.HeavyMemory => "💾 大型轻量",
        ProcessClass.Interactive => "🖱 中型交互",
        ProcessClass.Background => "⚙ 小型后台",
        ProcessClass.Burst => "⚡ 突发型",
        ProcessClass.System => "🛡 系统",
        _ => "❓ 未知"
    };
    private static Dictionary<ProcessClass, int> _classSummary;
    private static DateTime _classSummaryAt = DateTime.MinValue;
    private static readonly object _classSummaryLock = new();
    public static Dictionary<ProcessClass, int> GetClassificationSummary()
    {
        // ★ 3.6.8 性能: UI 每 3 秒刷新一次，原实现每次对全部档案（可达 2000 个）
        //   重新跑滑窗统计；改为 10 秒缓存后开销降到 1/3 以下。
        lock (_classSummaryLock)
        {
            if (_classSummary != null && (DateTime.Now - _classSummaryAt).TotalSeconds < 10)
                return new Dictionary<ProcessClass, int>(_classSummary);
        }
        var summary = new Dictionary<ProcessClass, int>();
        foreach (var kv in _collector.GetAllProcesses())
        {
            var cls = _models.Classify(_collector, kv.Key);
            summary[cls] = summary.GetValueOrDefault(cls) + 1;
        }
        var result = summary.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        lock (_classSummaryLock) { _classSummary = result; _classSummaryAt = DateTime.Now; }
        return new Dictionary<ProcessClass, int>(result);
    }
    /// <summary>★ 发布命中/抛空事件（闭合决策日志）</summary>
    public static void OnFeedback(string processName, bool hit, double confidence)
    {
        try
        {
            _feedback.Record(processName, hit, confidence);
            _collector.OnFeedback(processName, hit);
            EventBus.Publish(new PredictionResultEvent(processName, hit, confidence,
                DateTime.Now, EventBus.NextSeq()));
        }
        catch (Exception ex) { AppLogger.Debug($"反馈异常: {ex.Message}"); }
    }
    public static double GetConfidence(string processName) => _feedback.GetConfidence(processName);
    /// <summary>★ 3.6.8: 预测命中率（与预加载成功率严格区分）</summary>
    public static void OnRuleHit(string processName) => _collector.OnRuleHit(processName);
    /// <summary>★ 3.6.8: 用户分类覆盖（原本只有读取方，无写入入口）</summary>
    public static void SetUserOverride(string processName, string classification)
    {
        _collector.SetUserOverride(processName, classification);
        // ★ 3.6.8: 用户覆盖后立即失效策略缓存
        lock (_strategyLock) _strategyCache.Clear();
    }
    public static string GetScene() => _collector.GetCurrentScene();
    public static string GetStats()
        => $"🧠 {_collector.GetProcessCount()} 进程, {_models.GetPatternCount()} 模式, " +
           $"{_feedback.GetConfidenceCount()} 置信度, 命中率 {_feedback.GetHitRate():P0}";
    /// <summary>★ 3.6.8: 闲时学习统计（供诊断页展示，避免 GetStats 成为死 API）</summary>
    public static string GetLearningStats() => _learner.GetStats();
    public static bool Save()
    {
        try
        {
            // ★ 3.6.8 性能: 采集关闭时学习数据不会增长，原实现仍每 5 分钟重写
            //   learning_data.json（约 6MB/h 的无谓写盘）。首次保存后即跳过。
            if (!AppConfig.EnableDataCollection && _savedOnce) return false;
            var data = new LearningData();
            _collector.Export(data); _models.Export(data); _feedback.Export(data);
            data.LastSave = DateTime.Now;
            _persistence.ReplaceData(data);
            bool ok = _persistence.Save();
            if (ok) _savedOnce = true;
            return ok;
        }
        catch (Exception ex) { AppLogger.Error($"学习数据保存失败: {ex.Message}"); return false; }
    }
    public static void Shutdown()
    {
        _idleTimer?.Dispose(); _flushTimer?.Dispose();
        Save();
        _persistence.FlushAndStop();
    }
    private static async Task TryIdleLearning()
    {
        var (cpu, mem) = Infrastructure.Cache.CacheManager.GetSystemState();
        if (cpu > Constants.IDLE_CPU_THRESHOLD || mem < Constants.IDLE_MEMORY_THRESHOLD) return; // ★ 具名
        if (!AppConfig.EnableIdleLearning) return;
        // 没有采集数据时（默认配置）跳过，避免空转与无意义写盘
        if (!AppConfig.EnableDataCollection && _collector.GetProcessCount() == 0) return;
        await _learner.Execute(_collector, _models, _feedback);
        Save();
    }
}
}