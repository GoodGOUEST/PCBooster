// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Execution/Preload/UnifiedPreloader.cs
// 统一预加载器
// 通用治理: 删除名字豁免——LockProcessMemory 已只抬下限不限增长，全应用安全
// 3.6.8 修复:
// ① 结果三态化（跳过/成功/失败）——原来"已在内存""去重命中"都返回 true，
// 导致决策效果与成功率虚高；
// ② 文件触碰上限 100MB→8MB 且启用 SequentialScan（见 NativeApi.PreloadProcess）；
// ③ 预加载 = 内存驻留预热，不会启动未运行的程序（与文案一致）。
// ============================================================
using System.Threading;
using System.Threading.Tasks;
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;
namespace PCBooster.Execution.Preload
{
/// <summary>预加载结果（区分跳过，避免把"不需要"算成"成功"）</summary>
public enum PreloadOutcome { Skipped, Preloaded, Failed }
/// <summary>统一预加载器</summary>
public static class UnifiedPreloader
{
private static int _activePreloads;
private static int _preloadSuccess, _preloadFail;
private static int _skipDedup, _skipInMemory, _skipNotAllowed, _skipBusy;
    private static bool TryBeginPreload()
    {
        int next = Interlocked.Increment(ref _activePreloads);
        if (next <= Constants.PRELOAD_MAX_CONCURRENT) return true;
        Interlocked.Decrement(ref _activePreloads);
        return false;
    }
    private static void EndPreload() => Interlocked.Decrement(ref _activePreloads);
    /// <summary>三态返回（调度器据此决定是否计入决策效果）</summary>
    public static async Task<PreloadOutcome> TryPreloadAsync(
        int pid, string processName, double confidence,
        Action<string> logCallback = null, bool isWarmup = false)
    {
        if (!TryBeginPreload())
        {
            _skipBusy++;
            return PreloadOutcome.Skipped;
        }
        try
        {
            var strategy = IntelligenceEngine.GetStrategy(processName);
            if (!strategy.CanPreload)
            {
                _skipNotAllowed++;
                logCallback?.Invoke($"⏭ 跳过预加载: {processName} (分类不允许)");
                return PreloadOutcome.Skipped;
            }
            if (!ProcessSafeHelper.IsAlive(pid)) return PreloadOutcome.Skipped;
            // 通用缓存层做重复预加载去重（10 分钟内同一进程不重复预热）
            string cacheKey = "preloaded:" + processName;
            bool cacheOn = AppConfig.GetBool(ConfigSchema.EnableMetadataCache, true);
            if (cacheOn && CacheManager.Get<string>(cacheKey) != null)
            {
                _skipDedup++;
                return PreloadOutcome.Skipped;
            }
            long ws = ProcessSafeHelper.GetWorkingSet(pid);
            bool isGame = ProcessMatcher.IsGame(processName);
            long skipThreshold = (isGame
                ? Constants.PRELOAD_SKIP_THRESHOLD_GAME_MB
                : Constants.PRELOAD_SKIP_THRESHOLD_MB) * 1024 * 1024;
            if (ws > skipThreshold)
            {
                _skipInMemory++;
                logCallback?.Invoke($"⏭ 已在内存: {processName} ({ws / 1024 / 1024}MB)");
                return PreloadOutcome.Skipped;
            }
            bool success = await Task.Run(() =>
                ExecutePreload(pid, processName, confidence, isWarmup));
            if (success)
            {
                PreloadProtection.Protect(pid, confidence, processName);
                _preloadSuccess++;
                if (cacheOn) CacheManager.Set(cacheKey, "1", TimeSpan.FromMinutes(10));
                EventBus.Publish(new PreloadCompletedEvent(pid, processName, true, 0,
                    DateTime.Now, EventBus.NextSeq()));
                logCallback?.Invoke($"✅ 预加载成功: {processName} ({confidence:P0})");
                return PreloadOutcome.Preloaded;
            }
            _preloadFail++;
            logCallback?.Invoke($"❌ 预加载失败: {processName}");
            return PreloadOutcome.Failed;
        }
        catch (Exception ex)
        {
            _preloadFail++;
            AppLogger.Debug($"预加载异常 {processName}: {ex.Message}");
            return PreloadOutcome.Failed;
        }
        finally { EndPreload(); }
    }
    /// <summary>兼容接口：仅"真正执行了预加载"返回 true</summary>
    public static async Task<bool> PreloadForProcess(
        int pid, string processName, double confidence,
        Action<string> logCallback = null, bool isWarmup = false)
        => await TryPreloadAsync(pid, processName, confidence, logCallback, isWarmup)
           == PreloadOutcome.Preloaded;
    /// <summary>内联执行器（文件触碰 + 内存锁定）</summary>
    private static bool ExecutePreload(int pid, string processName, double confidence, bool isWarmup)
    {
        bool fileTouched = NativeApi.PreloadProcess(pid);
        bool memoryLocked = NativeApi.LockProcessMemory(pid); // max=-1，无天花板
        return fileTouched || memoryLocked;
    }
    /// <summary>预加载成功率（成功 / (成功+失败)），与"预测命中率"严格区分</summary>
    public static double GetHitRate()
    {
        int total = _preloadSuccess + _preloadFail;
        return total > 0 ? (double)_preloadSuccess / total : 0;
    }
    public static string GetStats()
        => $"📥 预加载: 成功 {_preloadSuccess}, 失败 {_preloadFail}, 成功率 {GetHitRate():P0}" +
           $" | 跳过: 已在内存 {_skipInMemory}, 去重 {_skipDedup}, 策略禁 {_skipNotAllowed}, 并发满 {_skipBusy}";
}
}
