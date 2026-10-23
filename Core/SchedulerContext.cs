// ============================================================
// PC Booster v3.6.8
// Core/SchedulerContext.cs
// 调度上下文（周期级只读快照，UI 与决策同源，修复 U8）
// ============================================================

namespace PCBooster.Core
{
    /// <summary>调度上下文（只读视图）</summary>
    public class SchedulerContext
    {
        public double CpuUsage { get; init; }
        public double MemoryAvailable { get; init; }
        public int ForegroundPid { get; init; }
        public string ForegroundProcessName { get; init; } = "";
        public List<(string process, double confidence)> Predictions { get; init; } = new();
        public AdaptiveScheduler.LoadLevel Level { get; init; }
        public List<int> PreloadPids { get; init; } = new();
        public string Scene { get; init; } = "";
        public double DataConfidence { get; init; } = 1.0;
        // ★ 3.6.8 修复: 删除 CollectedAt / Cycle（定义后从未被任何代码读取）
    }
}