// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/AppLogger.cs
// 日志（B模式: 日志目录改 %AppData%/PCBooster/logs）
// ============================================================

using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace PCBooster.Infrastructure.Common
{
    /// <summary>应用日志：Debug/Info 走队列，Error 直写</summary>
    public static class AppLogger
    {
        private static readonly BlockingCollection<string> _queue = new(Constants.MAX_LOG_QUEUE_SIZE);
        private static Thread _writerThread;
        private static string _currentFile;
        private static DateTime _currentDate;
        private static int _dropCount;
        private static volatile bool _isRunning;
        private static readonly object _fileLock = new();

        public static void Initialize()
        {
            _currentDate = DateTime.Now;
            _currentFile = GetFilePath(_currentDate);

            _isRunning = true;
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "LogWriter" };
            _writerThread.Start();

            CleanRetention();
            Info($"📝 日志系统已初始化 (保留 {Constants.LOG_RETENTION_DAYS} 天)");
        }

        public static void Debug(string message) => Enqueue("DEBUG", message);

        // 生产限流：同一条 DEBUG 在 60 秒内只写一次。
        // 典型场景：对受保护/已退出进程查信息必然抛异常，逐次记录会把日志刷爆。
        private static readonly Dictionary<string, DateTime> _debugLastWrite = new(StringComparer.Ordinal);
        private static readonly object _debugThrottleLock = new();

        /// <summary>同一条消息在 60 秒内只记录一次（用于可预期的、高频的异常）</summary>
        public static void DebugRateLimited(string message)
        {
            var now = DateTime.Now;
            lock (_debugThrottleLock)
            {
                if (_debugLastWrite.TryGetValue(message, out var last) && (now - last).TotalSeconds < 60) return;
                if (_debugLastWrite.Count > 256) _debugLastWrite.Clear();
                _debugLastWrite[message] = now;
            }
            Enqueue("DEBUG", message);
        }

        public static void Info(string message) => Enqueue("INFO", message);
        public static void Warning(string message) => Enqueue("WARN", message);

        /// <summary>关键日志不再在调用线程同步落盘（原实现会拖慢心跳线程）；
        /// 入队成功即返回，队列满或未初始化时退化为直写，关键日志仍不丢。</summary>
        public static void Error(string message)
        {
            var line = Format("ERROR", message);
            if (_isRunning && _queue.TryAdd(line)) return;
            WriteDirect(line);
        }

        private static void WriteDirect(string line)
        {
            try
            {
                var file = Volatile.Read(ref _currentFile);
                if (string.IsNullOrEmpty(file)) return;
                lock (_fileLock) File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { /* 日志子系统自身失败：不再记录，避免递归 */ }
        }

        private static void Enqueue(string level, string message)
        {
            if (!_isRunning) return;
            try
            {
                if (_queue.TryAdd(Format(level, message))) return;
            }
            catch (InvalidOperationException) { return; } // 关闭期间队列已 CompleteAdding
            if (Interlocked.Increment(ref _dropCount) == Constants.LOG_DROP_ALERT_THRESHOLD)
                Error($"⚠️ 日志队列溢出，已丢弃 {_dropCount} 条低级别日志");
        }

        private static string Format(string level, string message)
            => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        private static void WriterLoop()
        {
            var batch = new List<string>(Constants.LOG_BATCH_SIZE);
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    batch.Add(line);
                    while (batch.Count < Constants.LOG_BATCH_SIZE && _queue.TryTake(out var more, 100))
                        batch.Add(more);
                    WriteBatch(batch);
                    batch.Clear();
                }
            }
            catch { /* 日志子系统自身失败：不再记录，避免递归 */ }
        }

        private static void WriteBatch(List<string> batch)
        {
            try
            {
                RollDate();
                var file = Volatile.Read(ref _currentFile);
                if (string.IsNullOrEmpty(file)) return;
                // 与 Error 直写共用锁，避免两个写入者争抢文件句柄丢日志
                lock (_fileLock) File.AppendAllLines(file, batch);
            }
            catch { /* 日志子系统自身失败：不再记录，避免递归 */ }
        }

        private static void RollDate()
        {
            var today = DateTime.Now;
            if (today.Date != _currentDate.Date) { _currentDate = today; Volatile.Write(ref _currentFile, GetFilePath(today)); }
        }

        private static string GetFilePath(DateTime date)
            => Path.Combine(AppPaths.LogDir, $"booster_{date:yyyyMMdd}.log");

        private static void CleanRetention()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-Constants.LOG_RETENTION_DAYS);
                foreach (var file in Directory.GetFiles(AppPaths.LogDir, "booster_*.log"))
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch { /* 日志子系统自身失败：不再记录，避免递归 */ }
        }

        public static void Shutdown()
        {
            _isRunning = false;
            _queue.CompleteAdding();
            _writerThread?.Join(1000);
        }
    }
}
