// ============================================================
// PC Booster v3.6.8
// Core/DecisionNecessityEvaluator.cs
// 决策必要性分数（学习效果驱动）
// ============================================================

using PCBooster.Infrastructure.Common;

namespace PCBooster.Core
{
    /// <summary>决策必要性评估器</summary>
    public static class DecisionNecessityEvaluator
    {
        private static readonly Dictionary<string, double> _scores = new();
        private static readonly object _lock = new();
        private static DateTime _lastRecovery = DateTime.Now;

        public static bool ShouldExecute(string decision)
        {
            double score;
            lock (_lock)
            {
                RecoverIfNeededLocked();
                score = _scores.TryGetValue(decision, out var s) ? s : 0.5;
            }
            return score * DecisionEffectTracker.GetEffectiveness(decision) > Constants.NECESSITY_SKIP_THRESHOLD;
        }

        /// <summary>★ 3.6.8: 定期把低分向 0.5 拉回。
        /// 原实现只会“越失败越低”，一旦越过阈值就再也不会执行，
        /// 而不再执行又永远不会产生新数据 → 永久停用（与阈值棘轮同类问题）。</summary>
        private static void RecoverIfNeededLocked()
        {
            var now = DateTime.Now;
            if ((now - _lastRecovery).TotalSeconds < Constants.NECESSITY_RECOVERY_INTERVAL_SECONDS) return;
            _lastRecovery = now;
            foreach (var key in _scores.Keys.ToList())
                _scores[key] += (0.5 - _scores[key]) * Constants.NECESSITY_RECOVERY_PULL;
            DecisionEffectTracker.DecayAll(0.9);
        }

        public static void OnExecuted(string decision, bool hadEffect)
        {
            lock (_lock)
            {
                double cur = _scores.TryGetValue(decision, out var s) ? s : 0.5;
                _scores[decision] = hadEffect
                    ? Math.Min(1.0, cur + Constants.NECESSITY_BOOST_STEP)
                    : cur * Constants.NECESSITY_DECAY_FACTOR;
            }
        }

        public static string GetScoreReport()
        {
            lock (_lock)
            {
                if (_scores.Count == 0) return "必要性分数: 暂无数据";
                return "必要性分数:\n" + string.Join("\n",
                    _scores.Select(kv => $"  {kv.Key}: {kv.Value:F2}"));
            }
        }
    }
}