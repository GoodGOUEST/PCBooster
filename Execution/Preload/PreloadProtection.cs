// ============================================================
// PC Booster v3.6.8
// Execution/Preload/PreloadProtection.cs
// 预加载保护（动态时长 + 上限 + 锁外事件）
// 3.6.7: 0.5 基数具名化（PROTECTION_CONFIDENCE_BASE）
// ============================================================
using PCBooster.Infrastructure.Common;
using PCBooster.Intelligence;
namespace PCBooster.Execution.Preload
{
/// <summary>预加载保护</summary>
public static class PreloadProtection
{
private static readonly Dictionary<int, DateTime> _protectedPids = new();
    private static readonly object _lock = new();
    /// <summary>保护进程（动态保护时长 = 策略时长 × (基数+置信度)）</summary>
    public static void Protect(int pid, double confidence, string processName = "")
    {
        int baseDuration = Constants.PROTECTION_MIN_SECONDS;
        if (!string.IsNullOrEmpty(processName))
        {
            var strategy = IntelligenceEngine.GetStrategy(processName);
            baseDuration = strategy.ProtectionSeconds;
        }
        int duration = (int)(baseDuration * (Constants.PROTECTION_CONFIDENCE_BASE + confidence)); // ★ 具名
        duration = Math.Clamp(duration, Constants.PROTECTION_MIN_SECONDS, Constants.PROTECTION_MAX_SECONDS);
        lock (_lock)
        {
            var newExpiry = DateTime.Now.AddSeconds(duration);
            if (_protectedPids.TryGetValue(pid, out var current))
            {
                if (newExpiry > current) _protectedPids[pid] = newExpiry;
            }
            else _protectedPids[pid] = newExpiry;
        }
    }
    public static bool IsProtected(int pid)
    {
        lock (_lock)
            return _protectedPids.TryGetValue(pid, out var expiry) && expiry > DateTime.Now;
    }
    public static void CheckExpired()
    {
        List<int> expired;
        var now = DateTime.Now;
        lock (_lock)
        {
            expired = _protectedPids.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var pid in expired) _protectedPids.Remove(pid);
        }
        foreach (var pid in expired)
        {
            string name = ProcessSafeHelper.GetProcessNameWithExtension(pid);
            EventBus.Publish(new ProtectionExpiredEvent(pid, name, "timeout", DateTime.Now, EventBus.NextSeq()));
        }
    }
    public static int GetProtectedCount()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            return _protectedPids.Count(kv => kv.Value > now);
        }
    }
    public static string GetStats() => $"🛡 预加载保护: {GetProtectedCount()} 个进程";
}
}