// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Core/PreloadRegistry.cs
// 预加载 PID 注册表 —— 数据所有权归 Core 层
// 3.6.7 修复:
// ① 切断 Core → UI 反向依赖（原 MainForm._preloadPids 三处静态调用）
// ② 存活判定移出锁外，锁内不再有 OpenProcess 之类系统调用
// ③ 快照内已出现的 PID 直接视为存活，省去重复探测
// ④ 预加载名单 10 秒缓存（原每 2 秒 Split/ToHashSet 一次）
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;

namespace PCBooster.Core
{
    /// <summary>预加载 PID 注册表（线程安全；读返回副本，写加锁）</summary>
    public static class PreloadRegistry
    {
        private static readonly List<int> _pids = new();
        private static readonly object _lock = new();

        // 配置里的预加载名单每 2 秒被 Sync 重新解析，改为 10 秒缓存
        private static HashSet<string> _namesCache;
        private static DateTime _namesAt = DateTime.MinValue;

        private static HashSet<string> PreloadNames()
        {
            if (_namesCache != null && (DateTime.Now - _namesAt).TotalSeconds < 10)
                return _namesCache;
            var names = AppConfig.Get("PreloadList", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim().ToLowerInvariant())
                .ToHashSet();
            _namesCache = names;
            _namesAt = DateTime.Now;
            return names;
        }

        /// <summary>按配置项 PreloadList 装载（启动期调用一次）</summary>
        public static void Load()
        {
            var names = PreloadNames();
            if (names.Count == 0) return;

            // 锁外解析 PID（FindPidByName 命中快照字典，O(1)）
            var resolved = new List<int>(names.Count);
            foreach (var name in names)
            {
                int pid = NativeApi.FindPidByName(name);
                if (pid > 0) resolved.Add(pid);
            }

            lock (_lock)
            {
                foreach (int pid in resolved)
                    if (!_pids.Contains(pid)) _pids.Add(pid);
            }
        }

        /// <summary>快照（调用方拿到独立副本，不存在共享可变状态）</summary>
        public static List<int> Snapshot()
        {
            lock (_lock) return new List<int>(_pids);
        }

        /// <summary>按进程快照同步：清理已退出 PID，补入命名列表中新出现的 PID</summary>
        public static void Sync(IEnumerable<(int pid, string name)> snapshot)
        {
            if (snapshot == null) return;

            var names = PreloadNames();
            if (names.Count == 0) return;

            var items = snapshot as IList<(int pid, string name)> ?? snapshot.ToList();
            var aliveInSnapshot = new HashSet<int>(items.Count);
            foreach (var (pid, _) in items) aliveInSnapshot.Add(pid);

            // 关键: 存活判定全部在锁外完成（避免持锁做系统调用）
            List<int> current;
            lock (_lock) current = new List<int>(_pids);

            var keep = new List<int>(current.Count);
            foreach (int pid in current)
                if (aliveInSnapshot.Contains(pid) || ProcessSafeHelper.IsAlive(pid)) keep.Add(pid);

            // 补入命名列表中的新 PID（同一 PID 只保留一次）
            foreach (var (pid, name) in items)
            {
                if (pid <= 0 || string.IsNullOrEmpty(name)) continue;
                if (!names.Contains(name.ToLowerInvariant())) continue;
                if (!keep.Contains(pid)) keep.Add(pid);
            }

            lock (_lock)
            {
                _pids.Clear();
                _pids.AddRange(keep);
            }
        }

        public static void Clear()
        {
            lock (_lock) _pids.Clear();
        }
    }
}
