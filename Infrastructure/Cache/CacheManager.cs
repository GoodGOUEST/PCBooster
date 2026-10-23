// ============================================================
// PC Booster v3.6.8
// Infrastructure/Cache/CacheManager.cs
// 统一缓存（通用缓存 + 系统状态 + 预热）
// 3.6.7: 30s 清理间隔具名化（CACHE_CLEANUP_INTERVAL_SECONDS）
// ============================================================
using System.Collections.Concurrent;
using System.Threading.Tasks;
using PCBooster.Execution.Rules;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;
namespace PCBooster.Infrastructure.Cache
{
/// <summary>统一缓存管理器</summary>
public static class CacheManager
{
private class CacheEntry { public object Value; public DateTime ExpiresAt; }
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly object _cleanupLock = new();
    private static DateTime _lastCleanup = DateTime.Now;
    private static volatile bool _warmupCompleted;
    public static void Set<T>(string key, T value, TimeSpan ttl) where T : class
    {
        _cache[key] = new CacheEntry { Value = value, ExpiresAt = DateTime.Now + ttl };
        TryCleanup();
    }
    public static T Get<T>(string key) where T : class
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTime.Now) return entry.Value as T;
            _cache.TryRemove(key, out _);
        }
        return null;
    }
    private static void TryCleanup()
    {
        if ((DateTime.Now - _lastCleanup).TotalSeconds < Constants.CACHE_CLEANUP_INTERVAL_SECONDS) return;
        lock (_cleanupLock)
        {
            if ((DateTime.Now - _lastCleanup).TotalSeconds < Constants.CACHE_CLEANUP_INTERVAL_SECONDS) return;
            _lastCleanup = DateTime.Now;
            var now = DateTime.Now;
            foreach (var kv in _cache)
                if (kv.Value.ExpiresAt <= now) _cache.TryRemove(kv.Key, out _);
        }
    }
    public static (double cpu, double mem) GetSystemState(bool forceRefresh = false)
    {
        if (forceRefresh) NativeApi.RefreshMetricsInBackground();
        return (NativeApi.GetCpuUsage(), NativeApi.GetMemoryAvailablePercent());
    }
    public static bool IsWarmupCompleted => _warmupCompleted;
    public static async Task Warmup()
    {
        try
        {
            await Task.WhenAll(
                Task.Run(() => NativeApi.RefreshMetricsInBackground()),
                Task.Run(() => CpuTopology.Detect()));
            // ★ 3.6.8: 规则索引由 ProcessRuleEngine.Initialize 自己维护，
            //   这里不再反向依赖 Execution（去掉一处跨层反向依赖）。
        }
        catch (Exception ex) { AppLogger.Error($"缓存预热异常: {ex.Message}"); }
        finally { _warmupCompleted = true; }
    }
    public static string GetStats()
        => $"🗂 缓存: {_cache.Count} 项, 预热={(_warmupCompleted ? "完成" : "进行中")}";
}
}