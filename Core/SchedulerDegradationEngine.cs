// ============================================================
// PC Booster v3.6.8
// Core/SchedulerDegradationEngine.cs
// 降级评估引擎
// 3.6.7: 0.2/0.5/0.3 具名化（DEGRADE_*）
// ============================================================
using PCBooster.Infrastructure.Common;
namespace PCBooster.Core
{
/// <summary>降级评估引擎</summary>
public static class SchedulerDegradationEngine
{
public enum ScheduleLevel { Normal, Degraded, Safe, Minimal }
    public static ScheduleLevel Evaluate(SchedulerContext context, int healthy, int total)
    {
        if (context.DataConfidence < Constants.DEGRADE_MIN_CONFIDENCE) return ScheduleLevel.Minimal;
        if (total > 0 && (double)healthy / total < Constants.DEGRADE_HEALTHY_RATIO) return ScheduleLevel.Safe;
        bool decent = false;
        foreach (var p in context.Predictions)
            if (p.confidence > Constants.DEGRADE_DECENT_CONFIDENCE) { decent = true; break; }
        if (!decent) return ScheduleLevel.Degraded;
        return ScheduleLevel.Normal;
    }
    public static string GetLevelName(ScheduleLevel level) => level switch
    {
        ScheduleLevel.Normal => "正常",
        ScheduleLevel.Degraded => "降级",
        ScheduleLevel.Safe => "安全",
        ScheduleLevel.Minimal => "最小",
        _ => "⚪ 未知"
    };
}
}