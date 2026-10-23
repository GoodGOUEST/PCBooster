// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/MathToolkit.cs
// 滑动窗口 + 统计计算（泛型窗口 + 静态统计分离，修复 S3）
// ============================================================
namespace PCBooster.Infrastructure.Common
{
/// <summary>线程安全滑动窗口（泛型）</summary>
public sealed class SlidingWindow<T>
{
private readonly Queue<T> _queue = new();
private readonly int _maxSize;
private readonly object _lock = new();
    public SlidingWindow(int maxSize) { _maxSize = Math.Max(1, maxSize); }
    public void Add(T item)
    {
        lock (_lock)
        {
            _queue.Enqueue(item);
            while (_queue.Count > _maxSize) _queue.Dequeue();
        }
    }
    public void AddRange(IEnumerable<T> items)
    {
        lock (_lock)
        {
            foreach (var item in items) _queue.Enqueue(item);
            while (_queue.Count > _maxSize) _queue.Dequeue();
        }
    }
    public List<T> ToList() { lock (_lock) return _queue.ToList(); }
    public int Count { get { lock (_lock) return _queue.Count; } }
    public void Clear() { lock (_lock) _queue.Clear(); }
}
/// <summary>单遍统计摘要（不可变）</summary>
public readonly struct StatSummary
{
    public readonly double Avg, Max, Min, StdDev;
    public StatSummary(double avg, double max, double min, double std)
    { Avg = avg; Max = max; Min = min; StdDev = std; }
}
/// <summary>数值统计（纯静态，无状态）</summary>
public static class Stats
{
    /// <summary>单遍统计（消除多次 ToList/重复遍历）</summary>
    public static StatSummary Describe(IEnumerable<double> v)
    {
        var l = v as List<double> ?? v.ToList();
        if (l.Count == 0) return new StatSummary(0, 0, 0, 0);
        double sum = 0, max = double.MinValue, min = double.MaxValue;
        foreach (var x in l) { sum += x; if (x > max) max = x; if (x < min) min = x; }
        double avg = sum / l.Count;
        double sq = 0;
        foreach (var x in l) sq += (x - avg) * (x - avg);
        double std = l.Count > 1 ? Math.Sqrt(sq / (l.Count - 1)) : 0;
        return new StatSummary(avg, max, min, std);
    }
    public static double Average(IEnumerable<double> v)
    { var l = v.ToList(); return l.Count > 0 ? l.Average() : 0; }
    public static double Max(IEnumerable<double> v)
    { var l = v.ToList(); return l.Count > 0 ? l.Max() : 0; }
    public static double Min(IEnumerable<double> v)
    { var l = v.ToList(); return l.Count > 0 ? l.Min() : 0; }
    public static double StdDev(IEnumerable<double> v)
    {
        var l = v.ToList();
        if (l.Count < 2) return 0;
        double avg = l.Average();
        return Math.Sqrt(l.Sum(x => (x - avg) * (x - avg)) / (l.Count - 1));
    }
}
}
