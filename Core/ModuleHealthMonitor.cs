// ============================================================
// PC Booster v3.6.8
// Core/ModuleHealthMonitor.cs
// 模块健康监控（失败计数 + 隔离 + 恢复冷却）
// ============================================================

using PCBooster.Infrastructure.Common;

namespace PCBooster.Core
{
    /// <summary>模块健康监控器</summary>
    public static class ModuleHealthMonitor
    {
        private class HealthRecord
        {
            public int ConsecutiveFailures;
            public string Status = "Healthy";
            public DateTime IsolatedAt = DateTime.MinValue;
        }

        private static readonly Dictionary<string, HealthRecord> _records = new();
        private static readonly object _lock = new();

        /// <summary>★ 3.6.8: 订阅基建层故障通道（Execution 不反向依赖 Core）</summary>
        public static void EnableInfrastructureReporting()
            => Resilience.ModuleFault += RecordFailure;

        public static void RecordSuccess(string module)
        {
            string old = null, now = null;
            lock (_lock)
            {
                if (_records.TryGetValue(module, out var r))
                {
                    old = r.Status;
                    r.ConsecutiveFailures = 0;
                    r.Status = "Healthy";
                    now = r.Status;
                }
            }
            // ★ 3.6.8 修复: 恢复也发布事件（原来只有降级才发，UI 看不到恢复）
            if (old != null && old != now)
                EventBus.Publish(new ModuleHealthChangedEvent(module, now, 0, DateTime.Now, EventBus.NextSeq()));
        }

        public static void RecordFailure(string module)
        {
            string old = null; string now = null; int failures = 0;
            lock (_lock)
            {
                if (!_records.ContainsKey(module)) _records[module] = new HealthRecord();
                var r = _records[module];
                r.ConsecutiveFailures++;
                old = r.Status;
                if (r.ConsecutiveFailures >= Constants.MODULE_ISOLATED_THRESHOLD)
                { r.Status = "Isolated"; r.IsolatedAt = DateTime.Now; }
                else if (r.ConsecutiveFailures >= Constants.MODULE_DEGRADED_THRESHOLD)
                    r.Status = "Degraded";
                now = r.Status;
                failures = r.ConsecutiveFailures;   // ★ 3.6.8 修复: 原来硬编码传 0
            }
            if (old != now)
                EventBus.Publish(new ModuleHealthChangedEvent(module, now, failures, DateTime.Now, EventBus.NextSeq()));
        }

        public static bool IsAvailable(string module)
        {
            if (ModuleRegistry.IsCritical(module)) return true; // 关键模块永不隔离
            lock (_lock)
            {
                if (!_records.TryGetValue(module, out var r)) return true;
                if (r.Status != "Isolated") return true;
                if ((DateTime.Now - r.IsolatedAt).TotalSeconds > Constants.MODULE_RECOVERY_INTERVAL_SECONDS)
                { r.Status = "Degraded"; r.ConsecutiveFailures = Constants.MODULE_DEGRADED_THRESHOLD; return true; }
                return false;
            }
        }

        public static (int healthy, int total) GetHealthSummary()
        {
            lock (_lock)
            {
                int total = ModuleRegistry.TotalMonitoredModules;
                int healthy = ModuleRegistry.AllModules.Count(m => IsAvailable(m));
                return (healthy, total);
            }
        }

        public static string GetReport()
        {
            var (h, t) = GetHealthSummary();
            return $"🏥 模块健康: {h}/{t} 健康";
        }
    }
}