// ============================================================
// PC Booster v3.6.8
// Intelligence/LearningScheduler.cs
// 统一学习调度（闲时学习 + 模式固化 + 衰减）
// 3.6.7: D11 IdleLearningCompletedEvent 回传真实固化数；
//        固化阈值/优先级具名化（LEARN_RULE_*）
// ============================================================
using System.Threading.Tasks;
using PCBooster.Infrastructure.Common;
namespace PCBooster.Intelligence
{
/// <summary>统一学习调度器</summary>
public class LearningScheduler
{
private int _totalRuns;
private long _totalMs;
    public async Task Execute(DataCollector collector, ModelEngine models, FeedbackLoop feedback)
    {
        var start = DateTime.Now;
        int converted = 0;
        try
        {
            await Task.Run(() => { feedback.DecayAll(); collector.CleanStale(); });
            await Task.Run(() => models.UpdateModels(collector));
            await Task.Run(() =>
            {
                converted = ConvertPatternsToRules(models); // ★ D11 捕获
                if (converted > 0) AppLogger.Info($"📋 模式固化: {converted} 个");
            });
            var duration = (DateTime.Now - start).TotalMilliseconds;
            _totalRuns++;
            _totalMs += (long)duration;
            EventBus.Publish(new IdleLearningCompletedEvent((int)duration, converted,
                DateTime.Now, EventBus.NextSeq())); // ★ D11
        }
        catch (Exception ex) { AppLogger.Error($"闲时学习异常: {ex.Message}"); }
    }
    /// <summary>周期模式 → 规则固化</summary>
    private int ConvertPatternsToRules(ModelEngine models)
    {
        int converted = 0;
        var patterns = models.GetPeriodicPatterns();
        var existing = Execution.Rules.ProcessRuleEngine.GetAllRules();
        foreach (var pattern in patterns)
        {
            if (pattern.Confidence < Constants.LEARNING_QUALITY_THRESHOLD) continue;
            if (pattern.Occurrences < Constants.LEARN_RULE_MIN_OCCURRENCES) continue;
            if (existing.Any(r => r.Name == $"学习: {pattern.ProcessName}" &&
                r.ProcessPattern == pattern.ProcessName)) continue;
            var rule = new Execution.Rules.ProcessRule
            {
                Name = $"学习: {pattern.ProcessName}",
                ProcessPattern = pattern.ProcessName,
                IsEnabled = true,
                Priority = Constants.LEARN_RULE_PRIORITY,
                Source = "learn",
                // ★ 3.6.8 修复: 只做预加载，不再隐式改优先级（TargetPriority=-1）
                Action = "preload",
                TargetPriority = -1
            };
            Execution.Rules.ProcessRuleEngine.AddRule(rule);
            converted++;
        }
        if (converted > 0) Execution.Rules.ProcessRuleEngine.Save();
        return converted;
    }
    public string GetStats()
    {
        double avg = _totalRuns > 0 ? (double)_totalMs / _totalRuns : 0;
        return $"🧠 学习: {_totalRuns} 次, 平均 {avg:F0}ms";
    }
}
}