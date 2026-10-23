// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/EventBus.cs
// 事件总线（异步发布 + 关键事件有序 + 锁外回调）
// ============================================================

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>事件总线：普通事件异步并发，关键事件单线程有序</summary>
    public static class EventBus
    {
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private static readonly object _lock = new();
        private static long _seq;
        private static volatile bool _isShuttingDown;

        // T5/U6修复: 关键事件有序通道（单消费者保证顺序）
        private static readonly BlockingCollection<Action> _orderedQueue = new();
        private static readonly Thread _orderedWorker;

        static EventBus()
        {
            _orderedWorker = new Thread(OrderedLoop) { IsBackground = true, Name = "EventBus_Ordered" };
            _orderedWorker.Start();
        }

        /// <summary>序列号生成（事件排序依据）</summary>
        public static long NextSeq() => Interlocked.Increment(ref _seq);

        public static void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                { list = new List<Delegate>(); _handlers[typeof(TEvent)] = list; }
                list.Add(handler);
            }
        }

        /// <summary>M7修复: 支持退订（页面销毁时释放）</summary>
        public static void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                    list.Remove(handler);
            }
        }

        public static void Publish<TEvent>(TEvent evt) where TEvent : class
        {
            if (_isShuttingDown) return;

            List<Delegate> snapshot;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0) return;
                snapshot = new List<Delegate>(list);
            }

            // T5修复: 关键状态事件走有序通道，其余异步并发
            bool isCritical = evt is SceneChangedEvent || evt is PowerStateChangedEvent;
            if (isCritical) _orderedQueue.TryAdd(() => InvokeAll(snapshot, evt));
            else Task.Run(() => InvokeAll(snapshot, evt));
        }

        /// <summary>批量发布——同帧同类事件合并为一次 Task
        ///（原实现单帧最多 50 个 launch 事件 → 50 个线程池任务）</summary>
        public static void PublishBatch<TEvent>(IEnumerable<TEvent> events) where TEvent : class
        {
            if (_isShuttingDown || events == null) return;
            List<Delegate> snapshot;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0) return;
                snapshot = new List<Delegate>(list);
            }
            var items = events as IList<TEvent> ?? events.ToList();
            if (items.Count == 0) return;
            Task.Run(() =>
            {
                for (int i = 0; i < items.Count; i++)
                    InvokeAll(snapshot, items[i]);
            });
        }

        /// <summary>强类型委托直调，去掉 DynamicInvoke 反射开销
        /// （高频事件如 ProcessLaunchedEvent 单帧可达数十次）</summary>
        private static void InvokeAll<TEvent>(List<Delegate> snapshot, TEvent evt) where TEvent : class
        {
            foreach (var handler in snapshot)
            {
                try
                {
                    if (handler is Action<TEvent> typed) typed(evt);
                    else handler.DynamicInvoke(evt);
                }
                catch (Exception ex) { AppLogger.Error($"EventBus 回调异常: {ex.Message}"); }
            }
        }

        private static void OrderedLoop()
        {
            try { foreach (var action in _orderedQueue.GetConsumingEnumerable()) action(); }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static void Clear()
        {
            _isShuttingDown = true;
            _orderedQueue.CompleteAdding();
            lock (_lock) { _handlers.Clear(); }
        }
    }
}
