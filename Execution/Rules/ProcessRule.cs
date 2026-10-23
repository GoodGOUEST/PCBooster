// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Execution/Rules/ProcessRule.cs
// 进程规则（数据类 + 条件匹配 + 优先级内联）
// ============================================================

using PCBooster.Infrastructure.Common;

namespace PCBooster.Execution.Rules
{
    /// <summary>进程规则</summary>
    public class ProcessRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; } = 5;

        public string ProcessPattern { get; set; } = "*";
        public string Condition { get; set; } = "";

        public string Action { get; set; } = "";
        // 默认 -1 = 不动作（原默认 2=Normal，导致新建/学习规则把所有匹配进程优先级重置）
        public int TargetPriority { get; set; } = -1;
        public long TargetAffinityMask { get; set; } = -1;

        public string Source { get; set; } = "user";
        public int HitCount { get; set; }
        public DateTime LastApplied { get; set; }

        public bool Matches(string processName)
        {
            if (!IsEnabled || string.IsNullOrEmpty(processName)) return false;
            return StringHelper.MatchWildcard(processName, ProcessPattern) ||
                   processName.Equals(ProcessPattern, StringComparison.OrdinalIgnoreCase);
        }

        public bool CheckCondition(double cpuUsage, double memoryAvailable)
        {
            if (string.IsNullOrEmpty(Condition)) return true;
            try
            {
                bool isOr = Condition.Contains(" OR ");
                var parts = Condition.Split(new[] { " AND ", " OR " }, StringSplitOptions.RemoveEmptyEntries);
                bool result = !isOr;
                foreach (var part in parts)
                {
                    bool pass = Evaluate(part.Trim(), cpuUsage, memoryAvailable);
                    result = isOr ? result || pass : result && pass;
                }
                return result;
            }
            catch { return true; }
        }

        private bool Evaluate(string condition, double cpu, double mem)
        {
            double value = ExtractNumber(condition);
            bool isCpu = condition.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);
            double actual = isCpu ? cpu : mem;
            if (condition.Contains(">=")) return actual >= value;
            if (condition.Contains("<=")) return actual <= value;
            if (condition.Contains(">")) return actual > value;
            if (condition.Contains("<")) return actual < value;
            return true;
        }

        private static double ExtractNumber(string condition)
        {
            foreach (var part in condition.Split(new[] { '>', '<', '=', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(part, out var v)) return v;
            return 0;
        }

        public string GetPriorityName() => TargetPriority switch
        {
            0 => "Idle", 1 => "BelowNormal", 2 => "Normal", 3 => "AboveNormal", 4 => "High", _ => "未指定"
        };

        /// <summary>动作归一化。空串 + 合法 TargetPriority 视为 priority（兼容历史规则）</summary>
        public string NormalizedAction()
        {
            var a = (Action ?? "").Trim().ToLowerInvariant();
            if (a.Length == 0 && TargetPriority >= 0 && TargetPriority <= 4) return "priority";
            return a;
        }

        /// <summary>无任何可执行动作（阻止 "*" 空规则影响全部进程）</summary>
        public bool IsNoOp => NormalizedAction().Length == 0 && TargetAffinityMask == -1;
    }
}
