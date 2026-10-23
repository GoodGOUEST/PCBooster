// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Core/AdaptiveScheduler.cs
// 自适应负载分级（2秒缓存 + 负载变化重置效果）
// ============================================================

using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;

namespace PCBooster.Core
{
    /// <summary>自适应调度器（负载分级）</summary>
    public static class AdaptiveScheduler
    {
        public enum LoadLevel { Idle, Light, Moderate, Heavy }

        private static LoadLevel _current = LoadLevel.Idle;
        private static DateTime _lastEval = DateTime.MinValue;

        public static LoadLevel GetCurrentLoadLevel()
        {
            if ((DateTime.Now - _lastEval).TotalSeconds < Constants.LOAD_CACHE_SECONDS)
                return _current;

            var (cpu, mem) = CacheManager.GetSystemState();
            var next = cpu switch
            {
                < Constants.CPU_IDLE_THRESHOLD => LoadLevel.Idle,
                < Constants.CPU_LIGHT_THRESHOLD => LoadLevel.Light,
                < Constants.CPU_MODERATE_THRESHOLD => LoadLevel.Moderate,
                _ => LoadLevel.Heavy
            };
            // 5 个百分点滞回——跨档后要再走出一段才确认，避免边界抖动
            if (next != _current)
            {
                double boundary = next switch
                {
                    LoadLevel.Idle => Constants.CPU_IDLE_THRESHOLD,
                    LoadLevel.Light => Constants.CPU_LIGHT_THRESHOLD,
                    LoadLevel.Moderate => Constants.CPU_MODERATE_THRESHOLD,
                    _ => Constants.CPU_MODERATE_THRESHOLD
                };
                bool up = (int)next > (int)_current;
                if (up && cpu < boundary + Constants.LOAD_LEVEL_HYSTERESIS) next = _current;
                else if (!up && cpu > boundary - Constants.LOAD_LEVEL_HYSTERESIS) next = _current;
            }

            if (next != _current)
            {
                var old = _current;
                _current = next;
                EventBus.Publish(new LoadLevelChangedEvent(old.ToString(), next.ToString(),
                    DateTime.Now, EventBus.NextSeq()));
                // 这里与 DecisionNecessityEvaluator 都在调用 DecayAll，同一份样本被衰减两次；
            // 衰减统一交给评估器（每小时一次），此处不再重复衰减。
            }

            _lastEval = DateTime.Now;
            return _current;
        }

        public static string GetLevelName(LoadLevel l) => l switch
        {
            LoadLevel.Idle => "空闲", LoadLevel.Light => "轻度",
            LoadLevel.Moderate => "中度", LoadLevel.Heavy => "重度", _ => "未知"
        };
    }
}
