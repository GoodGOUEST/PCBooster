// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/Events.cs
// 事件 record 全族集中定义（含 ProcessExitedEvent，修复 X3）
// 规范: 全部带默认序列号参数，构造点零改动（修复 H1）
// ============================================================

namespace PCBooster.Infrastructure.Common
{
    public record SceneChangedEvent(string OldScene, string NewScene, DateTime Timestamp, long Seq = 0);
    public record PowerStateChangedEvent(string OldState, string NewState, DateTime Timestamp, long Seq = 0);
    public record LoadLevelChangedEvent(string OldLevel, string NewLevel, DateTime Timestamp, long Seq = 0);
    public record PredictionResultEvent(string ProcessName, bool Hit, double Confidence, DateTime Timestamp, long Seq = 0);
    public record PreloadCompletedEvent(int Pid, string ProcessName, bool Success, long BytesRead, DateTime Timestamp, long Seq = 0);
    public record ProtectionExpiredEvent(int Pid, string ProcessName, string Reason, DateTime Timestamp, long Seq = 0);
    public record RuleAppliedEvent(string ProcessName, string RuleName, DateTime Timestamp, long Seq = 0);
    public record ModuleHealthChangedEvent(string ModuleName, string Status, int ConsecutiveFailures, DateTime Timestamp, long Seq = 0);
    public record IdleLearningCompletedEvent(int DurationMs, int PatternsMined, DateTime Timestamp, long Seq = 0);
    public record SystemWakeFromSleepEvent(long GapMs, DateTime Timestamp, long Seq = 0);
    public record ProcessLaunchedEvent(int Pid, string ProcessName, DateTime Timestamp, long Seq = 0);
    public record ProcessExitedEvent(int Pid, string ProcessName, DateTime Timestamp, long Seq = 0);
}
