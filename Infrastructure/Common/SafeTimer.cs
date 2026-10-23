// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/SafeTimer.cs
// 容错定时器（回调异常不炸线程池；周期内不重入）
// 本轮修复：
// 1) 一次休眠会被多个定时器各判一次时间跳变 → 唤醒事件做 10 秒去重
// 2) Dispose 等回调最多 500ms，5 个定时器串行让退出卡 2 秒 → 收到 50ms
// ============================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using PCBooster.Core;

namespace PCBooster.Infrastructure.Common
{
    public sealed class SafeTimer : IDisposable
    {
        private readonly string _name;
        private readonly Action _callback;
        private readonly Func<Task> _asyncCallback;
        private readonly string _healthModule;
        private readonly TimeSpan _dueTime;
        private readonly TimeSpan _period;

        private Timer _timer;
        private volatile bool _isRunning = true;
        private volatile bool _isDisposed;
        private int _isExecuting;
        private long _lastTick = Environment.TickCount64;
        private int _consecutiveFailures;

        // 静态去重戳 —— 多个定时器同时判时间跳变时只发一次唤醒事件
        private static long _lastWakePublished;

        public string Name => _name;

        public SafeTimer(string name, Action callback, TimeSpan dueTime, TimeSpan period,
            string moduleForHealth = null)
            : this(name, callback, null, dueTime, period, moduleForHealth)
        {
        }

        private SafeTimer(string name, Action callback, Func<Task> asyncCallback,
            TimeSpan dueTime, TimeSpan period, string moduleForHealth)
        {
            _name = name;
            _callback = callback;
            _asyncCallback = asyncCallback;
            _dueTime = dueTime;
            _period = period;
            _healthModule = moduleForHealth;
            _timer = new Timer(OnTick, null, _dueTime, _period);
            AppLogger.Debug($"⏱ 启动 {_name}（{_period.TotalMilliseconds}ms）");
        }

        public static SafeTimer Async(string name, Func<Task> callback, TimeSpan dueTime,
            TimeSpan period, string moduleForHealth = null)
            => new SafeTimer(name, null, callback, dueTime, period, moduleForHealth);

        private void OnTick(object state)
        {
            if (!_isRunning || _isDisposed) return;

            long now = Environment.TickCount64;
            long gap = now - _lastTick;
            _lastTick = now;

            // 休眠/时间跳变：跳过本周期，并通知上层“需要重新接管”
            if (gap > (long)(_period.TotalMilliseconds * Constants.TIME_LEAP_FACTOR))
            {
                Volatile.Write(ref _isExecuting, 0);
                try
                {
                    AppLogger.Warning($"⏱[{_name}] 时间跳跃 {gap}ms，跳过本周期");
                    long last = Volatile.Read(ref _lastWakePublished);
                    if (now - last > 10000 &&
                        Interlocked.CompareExchange(ref _lastWakePublished, now, last) == last)
                    {
                        EventBus.Publish(new SystemWakeFromSleepEvent(gap, DateTime.Now, EventBus.NextSeq()));
                    }
                }
                catch (Exception ex) { AppLogger.Error($"⏱[{_name}] 唤醒事件发布失败: {ex.Message}"); }
                return;
            }

            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
            {
                AppLogger.Debug($"[{_name}] 上周期未结束，跳过");
                return;
            }

            if (_asyncCallback != null)
            {
                _ = RunAsyncCore();
                return;
            }

            try
            {
                _callback?.Invoke();
                OnCallbackSucceeded();
            }
            catch (Exception ex)
            {
                OnCallbackFailed(ex);
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
            }
        }

        private async Task RunAsyncCore()
        {
            try
            {
                await _asyncCallback().ConfigureAwait(false);
                OnCallbackSucceeded();
            }
            catch (Exception ex)
            {
                OnCallbackFailed(ex);
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
            }
        }

        private void OnCallbackSucceeded()
        {
            if (_consecutiveFailures == 0) return;
            _consecutiveFailures = 0;
            // 恢复健康度（只有配置了模块名的定时器才上报）
            if (!string.IsNullOrEmpty(_healthModule)) ModuleHealthMonitor.RecordSuccess(_healthModule);
        }

        private void OnCallbackFailed(Exception ex)
        {
            if (++_consecutiveFailures >= 3)
            {
                AppLogger.Error($"⏱[{_name}] 回调连续失败 x{_consecutiveFailures}: {ex.Message}");
                ModuleHealthMonitor.RecordFailure(_healthModule);
            }
            else
            {
                AppLogger.Warning($"⏱[{_name}] 回调异常: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_isDisposed) return;
            _isRunning = false;
            try { _timer?.Change(-1, -1); }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isRunning = false;
            try
            {
                using var wait = new ManualResetEvent(false);
                _timer?.Dispose(wait);
                // 500ms → 50ms（退出时不让 5 个定时器串行拖 2 秒）
                wait.WaitOne(50);
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                _timer = null;
            }
        }
    }
}
