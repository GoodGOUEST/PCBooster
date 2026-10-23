// ============================================================
// PC Booster v3.6.8
// Core/DecisionEffectTracker.cs
// 决策效果跟踪（负载变化重置，防历史污染）
// ============================================================

using PCBooster.Infrastructure.Common;

namespace PCBooster.Core
{
    /// <summary>决策效果跟踪器</summary>
    public static class DecisionEffectTracker
    {
        private class EffectRecord { public int Total; public int Effective; }

        private static readonly Dictionary<string, EffectRecord> _records = new();
        private static readonly object _lock = new();

        public static void RecordResult(string decision, bool hadEffect)
        {
            lock (_lock)
            {
                if (!_records.ContainsKey(decision)) _records[decision] = new EffectRecord();
                var r = _records[decision];
                r.Total++;
                if (hadEffect) r.Effective++;
            }
        }

        public static double GetEffectiveness(string decision)
        {
            lock (_lock)
            {
                return _records.TryGetValue(decision, out var r) && r.Total > 0
                    ? (double)r.Effective / r.Total : 0.5;
            }
        }

        /// <summary>★ 3.6.8: 不再清空（保留空实现会误导后续调用）——请使用 DecayAll</summary>
        public static void DecayAll(double factor = 0.5)
        {
            lock (_lock)
            {
                foreach (var r in _records.Values)
                {
                    r.Total = Math.Max(1, (int)(r.Total * factor));
                    r.Effective = Math.Min(r.Total, (int)(r.Effective * factor));
                }
            }
        }

        public static string GetReport()
        {
            lock (_lock)
            {
                if (_records.Count == 0) return "📊 决策效果: 暂无数据";
                var lines = _records
                    .OrderByDescending(kv => (double)kv.Value.Effective / kv.Value.Total)
                    .Take(7)
                    .Select(kv => $"  {kv.Key}: {(double)kv.Value.Effective / kv.Value.Total:P0} ({kv.Value.Total}次)");
                return "📊 决策效果:\n" + string.Join("\n", lines);
            }
        }
    }
}