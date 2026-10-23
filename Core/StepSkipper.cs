// ============================================================
// PC Booster v3.6.8
// Core/StepSkipper.cs
// 跳过评估（深睡门控 + 降级门控 + 预热/无预测/重载门控）
// 3.6.8 修复: GetStats 改为真实计数（原为硬编码文案）；重载等级不再预加载
// ============================================================
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
namespace PCBooster.Core
{
/// <summary>步骤跳过评估器</summary>
public static class StepSkipper
{
// ★ 修复: 真实计数器（原 GetStats 返回硬编码字符串）
private static int _skipAllDeepSleep, _skipAllMinimal;
private static int _skipPreloadNoWarmup, _skipPreloadEmpty, _skipPreloadHeavy;
private static int _skipPreloadLowConf;
private static int _skipMemoryPressure;
public static bool ShouldSkipAll(SchedulerContext context,
SchedulerDegradationEngine.ScheduleLevel level)
{
if (PowerStateProxy.IsDeepSleep() &&
!context.Predictions.Any(p => p.confidence > Constants.SKIP_HIGH_CONFIDENCE) &&
!IsEngineColdStart())
{ _skipAllDeepSleep++; return true; }
        if (level == SchedulerDegradationEngine.ScheduleLevel.Minimal) { _skipAllMinimal++; return true; }
        return false;
    }
    public static bool ShouldSkipPreload(SchedulerContext context)
    {
        if (!CacheManager.IsWarmupCompleted) { _skipPreloadNoWarmup++; return true; }
        if (context.Predictions.Count == 0) { _skipPreloadEmpty++; return true; }
        // ★ 3.6.8 走查: 前台数据置信度过低（锁屏/安全桌面）时不做预加载
        if (context.DataConfidence < 0.2) { _skipPreloadLowConf++; return true; }
        // ★ 修复: 重载时不再预加载，避免与前台争抢
        if (context.Level == AdaptiveScheduler.LoadLevel.Heavy) { _skipPreloadHeavy++; return true; }
        return false;
    }
    public static bool ShouldSkipMemory(SchedulerContext context)
    {
        // ★ 3.6.8: 触发阈值开放给用户（默认可用内存 <60% 才考虑回收）
        double trigger = AppConfig.GetDouble(ConfigSchema.MemoryReclaimTrigger, Constants.MEMORY_RECLAIM_THRESHOLD);
        if (context.MemoryAvailable > trigger) { _skipMemoryPressure++; return true; }
        return false;
    }
    /// <summary>★ 3.6.8 走查: 只有开启采集时才存在“冷启动预热期”。
    /// 原实现采集关闭时 CollectedMinutes 恒为 0 → 永远算冷启动 → 深睡永不跳过工作。</summary>
    private static bool IsEngineColdStart()
        => AppConfig.EnableDataCollection &&
           Intelligence.IntelligenceEngine.GetCollectedMinutes() < Constants.COLD_START_MINUTES;
    public static string GetStats()
        => $"📊 跳过评估: 全跳过 {_skipAllDeepSleep + _skipAllMinimal} 次" +
           $" (深睡 {_skipAllDeepSleep} / 降级 {_skipAllMinimal})" +
           $" | 预加载跳过 {_skipPreloadNoWarmup + _skipPreloadEmpty + _skipPreloadHeavy + _skipPreloadLowConf}" +
           $" (未预热 {_skipPreloadNoWarmup} / 无预测 {_skipPreloadEmpty} / 重载 {_skipPreloadHeavy} / 低置信 {_skipPreloadLowConf})" +
           $" | 内存跳过 {_skipMemoryPressure}";
}
/// <summary>电源状态代理（避免 Core 直接依赖 Execution）</summary>
internal static class PowerStateProxy
{
    public static bool IsDeepSleep()
        => Execution.Power.PowerSaver.GetCurrentState() ==
           Execution.Power.PowerSaver.PowerState.DeepSleep;
}
}
