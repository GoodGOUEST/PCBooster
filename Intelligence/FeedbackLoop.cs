// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Intelligence/FeedbackLoop.cs
// 3.6.8 修复:
// ① 反馈对称：命中 +0.06 / 抛空 -0.03（原 0.05/-0.08 的均衡点高达 61.5%，
// 现实中命中率 20~35%，置信度必然沉底）
// ② 阈值双向自适应：按最近 40 次结果的命中率调整（<40% 升、>60% 降），
// 取代原先「只升不降」的棘轮（数天即可顶到上限并永久关闭预加载）
// ============================================================
using PCBooster.Infrastructure.Common;
namespace PCBooster.Intelligence
{
/// <summary>统一反馈闭环</summary>
public class FeedbackLoop
{
private readonly Dictionary<string, ConfidenceRecord> _confidences = new();
private readonly object _lock = new();
private int _totalHits, _totalMisses;
// 自适应阈值统计窗口（替代原 _consecutiveMisses 棘轮）
private int _winHits, _winTotal;
    private DateTime _lastThresholdSave = DateTime.MinValue;
    public class ConfidenceRecord
    {
        // 原来是 public 字段，System.Text.Json 默认只序列化属性，
        // 导致 learning_data.json 里每条置信度都是 {}，重启后全部回到 0.5。
        public double Value { get; set; } = 0.5;
        public int HitCount { get; set; }
        public int MissCount { get; set; }
        public double DecayRate { get; set; } = Constants.CONFIDENCE_DECAY_NORMAL;
    }
    public void Record(string processName, bool hit, double confidence)
    {
        // 置信度表原来用原始名做键，预测侧用小写规范化键查询，
        // WeChat/Code 等大写进程的置信度永远读不到（一直 0.5）。
        var key = StringHelper.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            if (!_confidences.ContainsKey(key)) _confidences[key] = new ConfidenceRecord();
            var r = _confidences[key];
            if (hit)
            {
                r.Value = Math.Min(Constants.CONFIDENCE_MAX, r.Value + Constants.CONFIDENCE_HIT_BONUS);
                r.HitCount++;
                _totalHits++;
                _winHits++;
                if (r.HitCount > Constants.FEEDBACK_HIGH_HIT_MIN_HITS &&
                    r.MissCount < r.HitCount * Constants.FEEDBACK_HIGH_HIT_MISS_RATIO)
                    r.DecayRate = Constants.CONFIDENCE_DECAY_HIGH_HIT;
            }
            else
            {
                r.Value = Math.Max(Constants.CONFIDENCE_MIN, r.Value - Constants.CONFIDENCE_MISS_PENALTY);
                r.MissCount++;
                _totalMisses++;
                if (r.MissCount > r.HitCount) r.DecayRate = Constants.CONFIDENCE_DECAY_LOW_HIT;
            }
            _winTotal++;
            if (_winTotal >= Constants.FEEDBACK_WINDOW_SAMPLES) AdjustThresholdLocked();
        }
    }
    /// <summary>双向自适应阈值（调用方已持锁）</summary>
    private void AdjustThresholdLocked()
    {
        double rate = (double)_winHits / _winTotal;
        _winHits = 0;
        _winTotal = 0;
        double cur = AppConfig.PreloadThreshold;
        bool changed = false;
        if (rate < Constants.FEEDBACK_LOW_HIT_RATE)
        {
            double v = Math.Min(Constants.PRELOAD_MAX_THRESHOLD, cur + Constants.THRESHOLD_STEP);
            if (v != cur) { AppConfig.PreloadThreshold = v; changed = true; }
        }
        else if (rate > Constants.FEEDBACK_HIGH_HIT_RATE)
        {
            double v = Math.Max(Constants.PRELOAD_MIN_THRESHOLD, cur - Constants.THRESHOLD_STEP);
            if (v != cur) { AppConfig.PreloadThreshold = v; changed = true; }
        }
        // 自适应结果定期落盘（原只在退出时保存，崩溃即丢失）
        if (changed && (DateTime.Now - _lastThresholdSave).TotalMinutes >= 5)
        {
            _lastThresholdSave = DateTime.Now;
            AppConfig.ForceSave();
        }
    }
    public double GetConfidence(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return Constants.CONFIDENCE_DEFAULT;
        lock (_lock)
        {
            // Record 存的是归一化键，这里以前直接查原名（大小写不同就永远拿默认值）
            string key = StringHelper.NormalizeProcessName(processName);
            return _confidences.TryGetValue(key, out var r) ? r.Value : Constants.CONFIDENCE_DEFAULT;
        }
    }
    public void DecayAll()
    {
        lock (_lock)
            foreach (var kv in _confidences)
                kv.Value.Value = Math.Max(Constants.CONFIDENCE_MIN, kv.Value.Value * kv.Value.DecayRate);
    }
    /// <summary>预测命中率（0~1）</summary>
    public double GetHitRate()
    {
        int total = _totalHits + _totalMisses;
        return total > 0 ? (double)_totalHits / total : 0;
    }
    public int GetConfidenceCount() { lock (_lock) return _confidences.Count; }
    public void Export(LearningData data)
    {
        lock (_lock)
        {
            // 复制一份，避免后台写盘与心跳修改同一批对象
            data.Confidences = _confidences.ToDictionary(kv => kv.Key, kv => new ConfidenceRecord
            {
                Value = kv.Value.Value,
                HitCount = kv.Value.HitCount,
                MissCount = kv.Value.MissCount,
                DecayRate = kv.Value.DecayRate
            });
            data.TotalHits = _totalHits;
            data.TotalMisses = _totalMisses;
        }
    }
    public void Load(LearningData data)
    {
        if (data?.Confidences == null) return;
        lock (_lock)
        {
            foreach (var kv in data.Confidences)
            {
                var key = StringHelper.NormalizeProcessName(kv.Key);   // 旧数据键也统一
                if (key.Length > 0) _confidences[key] = kv.Value;
            }
            _totalHits = data.TotalHits;
            _totalMisses = data.TotalMisses;
            _winHits = 0;
            _winTotal = 0;
        }
    }
}
}
