// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/Resilience.cs
// 并发容错（合并 LockGuard/RetryHelper/ExceptionHelper）
// ============================================================

using System.Threading;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>重试 + 安全执行 + 超时 + 原子操作</summary>
    public static class Resilience
    {
        // ═══════════ 安全执行 ═══════════

        public static T SafeExecute<T>(Func<T> func, T defaultValue = default, string ctx = "")
        {
            try { return func(); }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(ctx)) AppLogger.Debug($"[{ctx}] {ex.Message}");
                return defaultValue;
            }
        }

        public static bool SafeExecute(Action action, string ctx = "")
        {
            try { action(); return true; }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(ctx)) AppLogger.Debug($"[{ctx}] {ex.Message}");
                return false;
            }
        }

        // ═══════════ 原子操作（替代 LockGuard）═══════════

        public static bool TryAcquire(ref int flag)
            => Interlocked.CompareExchange(ref flag, 1, 0) == 0;

        public static void Release(ref int flag)
            => Volatile.Write(ref flag, 0);

        // ═══════════ 模块故障上报（）═══════════
        /// <summary>
        /// 基建层故障通道：Execution 只能依赖 Infrastructure，
        /// 不能反向引用 Core.ModuleHealthMonitor；由 Core 在启动时订阅本事件。
        /// </summary>
        public static event Action<string> ModuleFault;

        public static void ReportModuleFault(string module)
        {
            try { ModuleFault?.Invoke(module); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
