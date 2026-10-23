// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/ProcessSafeHelper.cs
// 进程安全操作（自动 Dispose + 异常捕获）
// 64位: SetAffinity 采用 ulong 掩码（v3.9.8）
// （无 return → 唯一匹配 Action<Process> 重载 → 返回 bool）
// ============================================================
using System.Diagnostics;
namespace PCBooster.Infrastructure.Common
{
    /// <summary>进程安全操作封装</summary>
    public static class ProcessSafeHelper
    {
        public static T WithProcess<T>(int pid, Func<Process, T> action, T defaultValue = default)
        {
            try { using var p = Process.GetProcessById(pid); return action(p); }
            catch { return defaultValue; }
        }
        public static bool WithProcess(int pid, Action<Process> action)
        {
            try { using var p = Process.GetProcessById(pid); action(p); return true; }
            catch { return false; }
        }
        public static string GetProcessName(int pid)
            => WithProcess(pid, p => p.ProcessName, "");
        public static string GetProcessNameWithExtension(int pid)
        {
            var name = GetProcessName(pid);
            return string.IsNullOrEmpty(name) ? "" : name + ".exe";
        }
        public static long GetWorkingSet(int pid)
            => WithProcess(pid, p => p.WorkingSet64, 0L);

        /// <summary>读取当前亲和性（0 表示读取失败）</summary>
        public static ulong GetAffinity(int pid)
            => WithProcess(pid, p => unchecked((ulong)(long)p.ProcessorAffinity), 0UL);
        public static bool IsAlive(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════
        // 游戏卡死防护：有“可见顶层窗口”的进程 = 用户正在用的程序，
        // 一律不施加 Job CPU/内存限制、不视为可回收候选。
        // 实测日志里 Games / PlatformProcess 这类未识别客户端被限速过——
        // 一旦打到游戏主进程上，表现就是画面卡住/黑屏。
        // ══════════════════════════════════════════════════════════
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private static HashSet<int> _windowPids = new();
        private static DateTime _windowPidsAt = DateTime.MinValue;
        private static readonly object _windowLock = new();
        /// <summary>该进程是否存在可见且有标题的顶层窗口（全盘 EnumWindows，30s 缓存）</summary>
        public static bool HasVisibleWindow(int pid)
        {
            if (pid <= 0) return false;
            HashSet<int> set;
            lock (_windowLock)
            {
                if ((DateTime.Now - _windowPidsAt).TotalSeconds >= 30)
                {
                    var found = new HashSet<int>();
                    try
                    {
                        EnumWindows((h, l) =>
                        {
                            try
                            {
                                if (!IsWindowVisible(h)) return true;
                                // 工具窗（输入法候选、各类隐形宿主）不算“用户窗口”
                                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                                // 用尺寸而不是标题来过滤：全屏游戏窗口经常标题为空
                                if (!GetWindowRect(h, out var r)) return true;
                                if (r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return true;
                                if (GetWindowThreadProcessId(h, out uint owner) != 0 && owner != 0)
                                    found.Add((int)owner);
                            }
                            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                            return true;
                        }, IntPtr.Zero);
                    }
                    catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                    _windowPids = found;
                    _windowPidsAt = DateTime.Now;
                }
                set = _windowPids;
            }
            return set.Contains(pid);
        }
        /// <summary>反作弊/保护进程一律不写（优先级/亲和性）</summary>
        private static bool IsProtectedPid(int pid)
        {
            var name = GetProcessName(pid);
            return !string.IsNullOrEmpty(name) && ProcessMatcher.IsProtected(name);
        }
        /// <summary>64位: ulong 亲和性掩码（原生寄存器宽，无符号溢出）</summary>
        public static bool SetAffinity(int pid, ulong mask)
        {
            if (IsProtectedPid(pid)) return false;
            return WithProcess(pid, p =>
            {
                // 首次修改前记录原值 + 进程身份（退出还原用）
                try
                {
                    lock (_restoreLock)
                    {
                        var o = OriginForLocked(pid, p);
                        if (!o.HasAffinity) { o.Affinity = p.ProcessorAffinity; o.HasAffinity = true; }
                    }
                }
                catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                p.ProcessorAffinity = unchecked((IntPtr)(long)mask);
            });
        }
        public static bool SetPriority(int pid, ProcessPriorityClass priority)
        {
            if (IsProtectedPid(pid)) return false;
            return WithProcess(pid, p =>
            {
                lock (_restoreLock)
                {
                    var o = OriginForLocked(pid, p);
                    if (!o.HasPriority) { o.Priority = p.PriorityClass; o.HasPriority = true; }
                }
                p.PriorityClass = priority;
            });
        }
        /// <summary>被改动前的原始状态（含进程身份，防 PID 复用误还原）</summary>
        private sealed class Origin
        {
            public string Name = "";
            public long StartTicks;
            public bool HasPriority;
            public ProcessPriorityClass Priority;
            public bool HasAffinity;
            public IntPtr Affinity;
        }
        private static readonly Dictionary<int, Origin> _origins = new();
        private static readonly object _restoreLock = new();
        private static Origin OriginForLocked(int pid, Process p)
        {
            if (_origins.TryGetValue(pid, out var o)) return o;
            o = new Origin();
            try { o.Name = p.ProcessName; } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { o.StartTicks = p.StartTime.Ticks; } catch { o.StartTicks = 0; }
            _origins[pid] = o;
            return o;
        }
        /// <summary>还原所有被修改过的进程优先级与亲和性，返回实际处理条数
        /// 先校验进程身份（名字/启动时间），避免 PID 被系统复用后改错进程</summary>
        public static int RestoreAll()
        {
            int restored = 0;
            int[] pids;
            Origin[] list;
            lock (_restoreLock)
            {
                if (_origins.Count == 0) return 0;
                pids = _origins.Keys.ToArray();
                list = _origins.Values.ToArray();
                _origins.Clear();
            }
            for (int i = 0; i < pids.Length; i++)
            {
                int pid = pids[i]; var o = list[i];
                bool ok = WithProcess(pid, p =>
                {
                    if (o.Name.Length > 0)
                    {
                        string cur = "";
                        try { cur = p.ProcessName; } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                        if (!string.Equals(cur, o.Name, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    if (o.StartTicks > 0)
                    {
                        long t = 0;
                        try { t = p.StartTime.Ticks; } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                        if (t != o.StartTicks) return;
                    }
                    if (o.HasPriority) p.PriorityClass = o.Priority;
                    if (o.HasAffinity) p.ProcessorAffinity = o.Affinity;
                });
                if (ok) restored++;
            }
            return restored;
        }
    }
}
