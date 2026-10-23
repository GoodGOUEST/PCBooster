// ============================================================
// PC Booster v3.6.8
// Intelligence/ModelEngine.cs
// ②根治: 策略表 Interactive/Unknown 内存帽 -1（提交帽只给确证后台类）
// + 四级分类漏斗 + 4参活体重载
// ============================================================
using PCBooster.Infrastructure.Common;
namespace PCBooster.Intelligence
{
/// <summary>统一模型引擎</summary>
public class ModelEngine
{
private readonly Dictionary<string, Dictionary<string, double>> _ngram1 = new();
private readonly List<PeriodicPattern> _patterns = new();
// ★ 3.6.8: 按进程的模式索引（预测热路径检索用）
private readonly Dictionary<string, List<PeriodicPattern>> _patternIndex = new();
private readonly object _lock = new();
    private static readonly Dictionary<ProcessClass, Strategy> _strategyMap = new()
    {
        [ProcessClass.HeavyCompute] = new Strategy
        { ProtectionSeconds = 180, CanReclaim = false, CanPreload = true, PreloadPriority = 2,
          JobCpuLimit = -1, JobMemoryLimitMB = -1, BoostPriority = true,
          CoreIsolation = true, LockFrequency = true, EnableWorkingSetLock = true, CacheAware = true },
        [ProcessClass.HeavyMemory] = new Strategy
        { ProtectionSeconds = 120, CanReclaim = true, CanPreload = true, PreloadPriority = 2,
          JobCpuLimit = 60, JobMemoryLimitMB = -1 },
        // ★ ②根治: 交互类不施加提交帽
        [ProcessClass.Interactive] = new Strategy
        { ProtectionSeconds = 60, CanReclaim = true, CanPreload = true, PreloadPriority = 1,
          JobCpuLimit = 50, JobMemoryLimitMB = -1 },
        // 后台类: 只做 CPU 限速，绝不加提交帽
        // ★ 3.6.8 修复: 原 JobMemoryLimitMB=512 会把多窗口浏览器/大型软件
        //   的后续内存分配直接判失败（表现为“切后台一会儿就被杀”）。
        [ProcessClass.Background] = new Strategy
        { ProtectionSeconds = 0, CanReclaim = true, CanPreload = false,
          JobCpuLimit = 30, JobMemoryLimitMB = -1 },
        [ProcessClass.Burst] = new Strategy
        { ProtectionSeconds = 30, CanReclaim = false, CanPreload = false,
          JobCpuLimit = -1, JobMemoryLimitMB = -1 },
        [ProcessClass.System] = new Strategy
        { ProtectionSeconds = 0, CanReclaim = false, CanPreload = false,
          JobCpuLimit = -1, JobMemoryLimitMB = -1 },
        // ★ ②根治: 未知类不施加提交帽（未确证不惩罚）
        [ProcessClass.Unknown] = new Strategy
        { ProtectionSeconds = 30, CanReclaim = true, CanPreload = false,
          JobCpuLimit = 50, JobMemoryLimitMB = -1 }
    };
    public List<(string process, double confidence)> Predict(DataCollector collector, int topN = 10)
    {
        if (collector == null || topN <= 0) return new List<(string, double)>();
        var predictions = new Dictionary<string, double>();
        var now = DateTime.Now;
        int timeSlot = now.Hour * (Constants.TIME_SLOTS_PER_DAY / 24) +
                       now.Minute / (1440 / Constants.TIME_SLOTS_PER_DAY);
        string scene = collector.GetCurrentScene();
        var recentSeq = collector.GetRecentSequence();
        var procs = collector.GetAllProcesses();
        // ★ 3.6.8: 去掉每次调用的模式列表/n-gram 深拷贝（O(P+E) GC 压力），
        //   改为锁内直读；模式检索走按进程的索引，不再 O(N×P) 线性扫描。
        lock (_lock)
        {
            Dictionary<string, double> trans = null;
            double transTotal = 0;
            if (recentSeq.Count > 0 && _ngram1.TryGetValue(recentSeq[^1], out var t))
            {
                trans = t;
                foreach (var v in t.Values) transTotal += v;
            }
            double transWeight = Constants.WEIGHT_SEQUENCE + Constants.WEIGHT_MARKOV;
            foreach (var kv in procs)
            {
                var data = kv.Value;
                double score = 0;
                int usageTotal = Math.Max(1, data.HourlyUsage.Sum());
                score += Math.Clamp((double)data.HourlyUsage[timeSlot] / usageTotal, 0, 1)
                    * Constants.WEIGHT_HOURLY;
                // ★ 3.6.8 修复: 周维度正式参与打分（原 DayOfWeek 字段只写不读）
                score += GetDayShare(data, now.DayOfWeek) * Constants.WEIGHT_WEEKDAY;
                // ★ 3.6.8 修复: 前台使用占比 + 最近使用——贴近“切窗口”习惯（原模型只看启动）
                score += GetForegroundScore(data) * Constants.WEIGHT_FOREGROUND;
                score += GetPeriodicScore(kv.Key, now.Hour, (int)now.DayOfWeek) * Constants.WEIGHT_PERIODIC;
                // ★ 3.6.8: 序列与马尔可夫本就是同一指标，合并计算（原两个方法结果相同）
                score += GetTransitionScore(trans, transTotal, kv.Key) * transWeight;
                score += (scene == Constants.SCENE_GAMING && ProcessMatcher.IsGame(kv.Key)
                    ? Constants.SCENE_GAME_BOOST : 0) * Constants.WEIGHT_SCENE;
                score += IntelligenceEngine.GetConfidence(kv.Key) * Constants.WEIGHT_CONFIDENCE;
                score += (data.TotalLaunches < Constants.EXPLORATION_MIN_LAUNCHES
                    ? Constants.EXPLORATION_BONUS : 0) * Constants.WEIGHT_EXPLORATION;
                predictions[kv.Key] = Math.Clamp(score, 0, 1);
            }
        }
        return predictions.OrderByDescending(x => x.Value).Take(topN)
            .Select(x => (x.Key, x.Value)).ToList();
    }
    /// <summary>★ 3.6.8: 按进程索引检索周期模式（原为每个进程线性扫全表）</summary>
    private double GetPeriodicScore(string process, int hour, int dayOfWeek)
    {
        if (!_patternIndex.TryGetValue(process, out var list) || list == null || list.Count == 0) return 0;
        double best = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p.DayOfWeek >= 0 && p.DayOfWeek != dayOfWeek) continue;
            if (Math.Abs(p.Hour - hour) > 1) continue;
            if (p.Confidence > best) best = p.Confidence;
        }
        return best;
    }
    /// <summary>★ 3.6.8: 该进程在指定星期的使用占比（0~1）</summary>
    private static double GetDayShare(DataCollector.ProcessData data, DayOfWeek day)
    {
        var weekly = data?.WeeklyUsage;
        if (weekly == null || weekly.Length != 7) return 0;
        int total = weekly.Sum();
        return total > 0 ? (double)weekly[(int)day] / total : 0;
    }

    /// <summary>★ 3.6.8: 前台使用得分（占比放大 + 最近使用加成）</summary>
    private static double GetForegroundScore(DataCollector.ProcessData data)
    {
        if (data == null) return 0;
        double share = data.TotalSamples > 0 ? (double)data.ForegroundCount / data.TotalSamples : 0;
        double score = Math.Clamp(share * Constants.FG_SHARE_GAIN, 0, 1);
        if (data.LastForegroundAt != DateTime.MinValue &&
            (DateTime.Now - data.LastForegroundAt).TotalMinutes <= Constants.FG_RECENT_MINUTES)
            score = Math.Min(1.0, score + 0.3);
        return score;
    }
    /// <summary>★ 3.6.8: 转移概率（0~1）；n-gram 存计数，只在打分时归一化</summary>
    private static double GetTransitionScore(Dictionary<string, double> trans, double total, string target)
    {
        if (trans == null || total <= 0) return 0;
        return trans.TryGetValue(target, out var v) ? v / total : 0;
    }
    // ═══════════ 分类（四级漏斗 + 4参活体）═══════════
    public ProcessClass Classify(DataCollector collector, string processName)
        => Classify(collector, processName, -1, -1, false);
    public ProcessClass Classify(DataCollector collector, string processName,
        long liveWs, double liveCpu, bool isForeground)
    {
        if (string.IsNullOrEmpty(processName)) return ProcessClass.Unknown;
        var data = collector?.GetProcess(processName);
        if (data != null && !string.IsNullOrEmpty(data.UserOverride) &&
            Enum.TryParse<ProcessClass>(data.UserOverride, out var uc)) return uc;

        // ★ 本轮修复: 游戏是“用户意图”（手动标记 / 游戏库已识别），不能被统计模型改判。
        //   旧顺序先跑 ClassifyByStats：游戏在加载、菜单、低帧阶段 CPU 采样可能偏低，
        //   会被判成 Interactive/Background → CoreIsolation / LockFrequency 全部不生效，
        //   表现就是“场景明明是游戏，但频率不锁、隔离不生效”。
        if (ProcessMatcher.IsSystemProcess(processName)) return ProcessClass.System;
        if (ProcessMatcher.IsGame(processName)) return ProcessClass.HeavyCompute;

        if (data != null && data.CpuWindow.Count >= Constants.PROFILER_MIN_SAMPLES)
        {
            var stat = ClassifyByStats(data);
            if (stat != ProcessClass.Unknown) return stat;
        }
        if (ProcessMatcher.IsDesignTool(processName) || ProcessMatcher.IsVideoEditor(processName))
            return ProcessClass.HeavyCompute;
        if (ProcessMatcher.IsBrowser(processName)) return ProcessClass.HeavyMemory;
        if (ProcessMatcher.IsDevTool(processName)) return ProcessClass.HeavyMemory;
        if (ProcessMatcher.IsOffice(processName) || ProcessMatcher.IsMeetingTool(processName))
            return ProcessClass.Interactive;
        if (liveWs >= 0)
        {
            bool cpuHot = liveCpu >= 0 && liveCpu > Constants.PROFILER_HIGH_CPU_THRESHOLD;
            if (liveWs > Constants.PROFILER_HEAVY_MEM_THRESHOLD)
                return cpuHot ? ProcessClass.HeavyCompute : ProcessClass.HeavyMemory;
            if (cpuHot && liveWs < Constants.PROFILER_MED_MEM_THRESHOLD) return ProcessClass.Burst;
            if (liveWs > Constants.PROFILER_MED_MEM_THRESHOLD)
                return isForeground ? ProcessClass.Interactive : ProcessClass.Background;
            return isForeground ? ProcessClass.Interactive : ProcessClass.Background;
        }
        return ProcessClass.Unknown;
    }
    private static ProcessClass ClassifyByStats(DataCollector.ProcessData data)
    {
        // ★ 3.6.8 性能: 单遍统计
        //（原实现：Window.ToList() + Select().ToList() + Average/Max/StdDev 各自内部再拷贝 = 6 个列表）
        var wsList = data.WorkingSetWindow.ToList();
        if (wsList.Count == 0) return ProcessClass.Unknown;
        var cpuList = data.CpuWindow.ToList();
        long avgWs = (long)wsList.Average();
        var cs = Stats.Describe(cpuList);   // Avg/Max/Min/StdDev 一次遍历，传入 List 不再拷贝
        double avgCpu = cs.Avg;
        double peakCpu = cs.Max;
        double volatility = cs.StdDev;
        double fgRatio = data.TotalSamples > 0 ? (double)data.ForegroundCount / data.TotalSamples : 0;
        if (avgWs > Constants.PROFILER_HEAVY_MEM_THRESHOLD &&
            (peakCpu > Constants.PROFILER_HIGH_CPU_THRESHOLD || avgCpu > Constants.PROFILER_HIGH_CPU_THRESHOLD * 0.7))
            return ProcessClass.HeavyCompute;
        if (avgWs > Constants.PROFILER_HEAVY_MEM_THRESHOLD && avgCpu < Constants.PROFILER_LOW_CPU_THRESHOLD)
            return ProcessClass.HeavyMemory;
        if (volatility > Constants.PROFILER_HIGH_VOLATILITY && data.TotalRunMinutes < 10 &&
            peakCpu > Constants.PROFILER_HIGH_CPU_THRESHOLD && avgCpu > 10)
            return ProcessClass.Burst;
        if (avgWs > Constants.PROFILER_MED_MEM_THRESHOLD && fgRatio > Constants.PROFILER_HIGH_FOREGROUND_RATIO)
            return ProcessClass.Interactive;
        if (avgWs < Constants.PROFILER_MED_MEM_THRESHOLD && fgRatio < Constants.PROFILER_LOW_FOREGROUND_RATIO)
            return ProcessClass.Background;
        return fgRatio > Constants.PROFILER_LOW_FOREGROUND_RATIO
            ? ProcessClass.Interactive : ProcessClass.Background;
    }
    public Strategy GetStrategy(ProcessClass cls)
        => _strategyMap.TryGetValue(cls, out var s) ? s : _strategyMap[ProcessClass.Unknown];

    /// <summary>★ 3.6.8 修复: 实时增量学习二元序列
    ///（原实现只在每 5 分钟闲时学最后一条转换，SEQUENCE+MARKOV 共 0.33 权重长期接近 0）</summary>
    public void LearnSequence(DataCollector collector)
    {
        if (collector == null) return;
        var seq = collector.GetRecentSequence();
        if (seq.Count < 2) return;
        var key = seq[^2]; var target = seq[^1];
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(target)) return;
        lock (_lock)
        {
            if (!_ngram1.TryGetValue(key, out var next))
            { next = new Dictionary<string, double>(); _ngram1[key] = next; }
            // ★ 3.6.8: 只累计计数（原实现每次写入都归一化，等价于丢掉历史权重）
            next[target] = next.GetValueOrDefault(target, 0) + 1;
        }
    }
    public void UpdateModels(DataCollector collector)
    {
        if (collector == null) return;
        var procs = collector.GetAllProcesses();
        lock (_lock)
        {
            // 1) 序列：只累计计数（归一化下移到打分时，避免每次写入都重算总和）
            var seq = collector.GetRecentSequence();
            if (seq.Count >= 2)
            {
                var key = seq[^2]; var target = seq[^1];
                if (!_ngram1.TryGetValue(key, out var next))
                { next = new Dictionary<string, double>(); _ngram1[key] = next; }
                next[target] = next.GetValueOrDefault(target, 0) + 1;
            }
            // 2) 周期模式：先建 (进程|小时) 索引，避免每进程每槽线性扫全表
            var byKey = new Dictionary<string, PeriodicPattern>(_patterns.Count + 16, StringComparer.Ordinal);
            for (int i = 0; i < _patterns.Count; i++)
            {
                var p = _patterns[i];
                byKey[p.ProcessName + "|" + p.Hour] = p;
            }
            int perHour = Constants.TIME_SLOTS_PER_DAY / 24;
            foreach (var kv in procs)
            {
                var usage = kv.Value.HourlyUsage;
                for (int slot = 0; slot < Constants.TIME_SLOTS_PER_DAY; slot++)
                {
                    int count = usage[slot];
                    if (count < 3) continue;
                    int hour = slot / perHour;
                    var k = kv.Key + "|" + hour;
                    double conf = Math.Min(Constants.PATTERN_CONFIDENCE_CAP,
                        count / Constants.PATTERN_OCCURRENCE_DIVISOR);
                    if (byKey.TryGetValue(k, out var existing))
                    {
                        existing.Occurrences = count;
                        existing.Confidence = conf;
                    }
                    else
                    {
                        var np = new PeriodicPattern
                        {
                            ProcessName = kv.Key,
                            DayOfWeek = -1,   // ★ 3.6.8: 周维度由 WeeklyUsage 单独打分
                            Hour = hour,
                            Occurrences = count,
                            Confidence = conf
                        };
                        _patterns.Add(np);
                        byKey[k] = np;
                    }
                }
            }
            // 3) 清理已消失进程的模式 + 上限保护（原来无界增长）
            // ★ 审计修正: procs 为空（采集关闭/尚未采集）时不能清理，否则会把已学模式全删
            if (_patterns.Count > 0 && procs.Count > 0)
            {
                _patterns.RemoveAll(p => !procs.ContainsKey(p.ProcessName));
                if (_patterns.Count > Constants.PATTERN_MAX_ENTRIES)
                    _patterns.RemoveRange(0, _patterns.Count - Constants.PATTERN_MAX_ENTRIES);
            }
            PruneNgramsLocked(Constants.NGRAM_MIN_OCCURRENCES, Constants.NGRAM_MAX_ENTRIES);
            RebuildPatternIndexLocked();
        }
    }
    /// <summary>★ 3.6.8: 重建按进程的模式索引（调用方已持锁）</summary>
    private void RebuildPatternIndexLocked()
    {
        _patternIndex.Clear();
        for (int i = 0; i < _patterns.Count; i++)
        {
            var p = _patterns[i];
            if (!_patternIndex.TryGetValue(p.ProcessName, out var list))
            { list = new List<PeriodicPattern>(4); _patternIndex[p.ProcessName] = list; }
            list.Add(p);
        }
    }
    private void PruneNgramsLocked(int minOccurrences, int maxEntries)
    {
        var remove = _ngram1.Where(kv => kv.Value.Count < minOccurrences).Select(kv => kv.Key).ToList();
        foreach (var k in remove) _ngram1.Remove(k);
        if (_ngram1.Count > maxEntries)
            foreach (var k in _ngram1.Keys.Take(_ngram1.Count - maxEntries).ToList())
                _ngram1.Remove(k);
    }
    public int GetPatternCount() { lock (_lock) return _patterns.Count; }
    public List<PeriodicPattern> GetPeriodicPatterns() { lock (_lock) return new List<PeriodicPattern>(_patterns); }
    public void Export(LearningData data) { lock (_lock) data.PeriodicPatterns = new List<PeriodicPattern>(_patterns); }
    public void Load(LearningData data)
    {
        if (data?.PeriodicPatterns == null) return;
        lock (_lock)
        {
            _patterns.AddRange(data.PeriodicPatterns);
            RebuildPatternIndexLocked();
        }
    }
}
}