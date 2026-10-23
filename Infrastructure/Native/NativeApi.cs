// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Native/NativeApi.cs
// ③根治: Lock 只抬下限(max=-1) / 新增 TrimWorkingSet(修剪后解除天花板)
// + 生命迹象信号 + GUID 标准格式 + volatile→Volatile + IsPidAliveCached
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PCBooster.Infrastructure.Common;
namespace PCBooster.Infrastructure.Native
{
/// <summary>原生 API + 后台指标采集（2秒周期）</summary>
public static class NativeApi
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    // 电池/交流电判定（电池供电时不启用激进睿频）
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, Reserved1;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    // 没有标准“高性能”方案时，复制当前方案做专用方案
    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, out IntPtr dest);
    // 读取活动方案 / 读取原停泊值 / 释放系统内存
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr schemeGuid);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr h, IntPtr min, IntPtr max, uint flags);
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    public struct ProcessSnapshot
    {
        public int Id;
        public string ProcessName;
        public long WorkingSet64;
        public ProcessPriorityClass PriorityClass;
    }
    private static volatile ProcessSnapshot[] _snapshot = Array.Empty<ProcessSnapshot>();
    private static volatile Dictionary<string, int> _pidIndex = new();
    private static volatile HashSet<int> _pidSet = new();
    private static volatile Dictionary<int, double> _procCpu = new();
    private static volatile HashSet<int> _wsGrowing = new();
    private static volatile Dictionary<int, long> _prevWs = new();
    private static readonly Dictionary<int, double> _cpuMax30 = new();
    private static readonly object _cpuLock = new();   // 保护 _cpuMax30/_lastCpuTime 的读写并发
    private static DateTime _cpuMax30WindowStart = DateTime.MinValue;
    private static double _cpuUsage;
    private static double _memAvailable = 50;
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static volatile bool _isRefreshing;
    private static readonly Dictionary<int, TimeSpan> _lastCpuTime = new();
    private static TimeSpan _lastTotalCpu;
    public static void RefreshMetricsInBackground()
    {
        if (_isRefreshing) return;
        if ((DateTime.Now - _lastRefresh).TotalSeconds < Constants.SNAPSHOT_CACHE_SECONDS) return;
        _isRefreshing = true;
        Task.Run(() =>
        {
            try
            {
                // 单遍遍历——TotalProcessorTime 与属性读取共用同一 Process 句柄
                //（原实现：属性一遍 + TotalProcessorTime 一遍 + Top128 又 GetProcessById 重开一遍）
                var procs = Process.GetProcesses();
                var snap = new List<ProcessSnapshot>(procs.Length);
                var procTimes = new Dictionary<int, TimeSpan>(procs.Length);
                var cur = TimeSpan.Zero;
                try
                {
                    foreach (var p in procs)
                    {
                        int id = 0;
                        try
                        {
                            id = p.Id;
                            var entry = new ProcessSnapshot
                            {
                                Id = id, ProcessName = p.ProcessName,
                                WorkingSet64 = p.WorkingSet64,
                                PriorityClass = ProcessPriorityClass.Normal
                            };
                            // 受保护/提权进程可能拒绝读优先级，
                            // 原实现因此丢掉整条快照（进程“看不见”）。只降级该字段。
                            try { entry.PriorityClass = p.PriorityClass; } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                            snap.Add(entry);
                        }
                        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                        // 累计 CPU 时间单独兜底：部分受保护进程会拒绝此查询，
                        // 但不能因它丢失整个快照条目（与原行为一致）
                        try
                        {
                            if (id > 0)
                            {
                                var tp = p.TotalProcessorTime;
                                cur += tp;
                                procTimes[id] = tp;
                            }
                        }
                        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                    }
                }
                finally
                {
                    // 及时释放 Process 句柄（原实现靠终结器兜底）
                    foreach (var p in procs) { try { p.Dispose(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); } }
                }
                var index = new Dictionary<string, int>(snap.Count);
                var pidSet = new HashSet<int>(snap.Count);
                var prev = _prevWs;
                var growing = new HashSet<int>();
                foreach (var s in snap)
                {
                    pidSet.Add(s.Id);
                    if (prev.TryGetValue(s.Id, out var pw) && s.WorkingSet64 > pw * 1.02)
                        growing.Add(s.Id);
                    if (string.IsNullOrEmpty(s.ProcessName)) continue;
                    var key = StringHelper.NormalizeProcessName(s.ProcessName);
                    if (!index.ContainsKey(key)) index[key] = s.Id;
                }
                var now = DateTime.Now;
                if (_lastRefresh != DateTime.MinValue)
                {
                    double wall = (now - _lastRefresh).TotalMilliseconds * Environment.ProcessorCount;
                    if (wall > 0)
                        System.Threading.Volatile.Write(ref _cpuUsage,
                            Math.Clamp((cur - _lastTotalCpu).TotalMilliseconds / wall * 100, 0, 100));
                }
                _lastTotalCpu = cur;
                var cpu = new Dictionary<int, double>(Constants.CPU_TOP_SAMPLE_COUNT);
                double wallMs = (now - _lastRefresh).TotalMilliseconds;
                var top = snap.OrderByDescending(s => s.WorkingSet64)
                              .Take(Constants.CPU_TOP_SAMPLE_COUNT);
                lock (_cpuLock)
                {
                    foreach (var s in top)
                    {
                        // 直接用首遍采集的累计 CPU 时间，不再重开进程句柄
                        if (!procTimes.TryGetValue(s.Id, out var t)) continue;
                        if (wallMs > 0 && _lastCpuTime.TryGetValue(s.Id, out var prevT))
                            cpu[s.Id] = Math.Clamp((t - prevT).TotalMilliseconds / wallMs * 100, 0, 100);
                        _lastCpuTime[s.Id] = t;
                    }
                    if ((now - _cpuMax30WindowStart).TotalSeconds > 30)
                    { _cpuMax30.Clear(); _cpuMax30WindowStart = now; }
                    foreach (var kv in cpu)
                        _cpuMax30[kv.Key] = Math.Max(_cpuMax30.GetValueOrDefault(kv.Key), kv.Value);
                    foreach (var k in _cpuMax30.Keys.ToList())
                        if (!pidSet.Contains(k)) _cpuMax30.Remove(k);
                    foreach (var k in _lastCpuTime.Keys.ToList())
                        if (!pidSet.Contains(k)) _lastCpuTime.Remove(k);
                }
                var info = new MEMORYSTATUSEX();
                info.dwLength = (uint)Marshal.SizeOf(info);
                if (GlobalMemoryStatusEx(ref info) && info.ullTotalPhys > 0)
                    System.Threading.Volatile.Write(ref _memAvailable,
                        (double)info.ullAvailPhys / info.ullTotalPhys * 100);
                _snapshot = snap.ToArray();
                _pidIndex = index;
                _pidSet = pidSet;
                _procCpu = cpu;
                _wsGrowing = growing;
                _prevWs = snap.ToDictionary(s => s.Id, s => s.WorkingSet64);
                _lastRefresh = now;
            }
            finally { _isRefreshing = false; }
        });
    }
    public static ProcessSnapshot[] GetSnapshotCached() => _snapshot;
    public static double GetCpuUsage() => System.Threading.Volatile.Read(ref _cpuUsage);
    public static double GetMemoryAvailablePercent() => System.Threading.Volatile.Read(ref _memAvailable);
    public static double GetProcessCpuCached(int pid)
        => _procCpu.TryGetValue(pid, out var v) ? v : 0;
    public static bool IsWsGrowingCached(int pid) => _wsGrowing.Contains(pid);
    public static double GetProcessCpuMax30Cached(int pid)
    { lock (_cpuLock) return _cpuMax30.TryGetValue(pid, out var v) ? v : 0; }
    public static bool IsPidAliveCached(int pid) => _pidSet.Contains(pid);
    public static int FindPidByName(string processName)
    {
        var key = StringHelper.NormalizeProcessName(processName);
        return _pidIndex.TryGetValue(key, out var pid) ? pid : 0;
    }
    public static int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }
    public static double GetGlobalIdleSeconds()
    {
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        if (!GetLastInputInfo(ref lii)) return 0;
        uint now = (uint)Environment.TickCount;
        return (now - lii.dwTime) / 1000.0;
    }
    // 进程所属会话号不变，只取一次（原实现每秒新建一个 Process 对象且不释放）
    private static uint _cachedSessionId;
    private static bool _sessionIdReady;
    public static bool IsSessionActive()
    {
        if (!_sessionIdReady)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                _cachedSessionId = (uint)self.SessionId;
                _sessionIdReady = true;   // 仅成功才标记，失败下次重试
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }
        uint active = WTSGetActiveConsoleSessionId();
        return active == _cachedSessionId && active != 0xFFFFFFFF;
    }
    // 标准 8-4-4-4-12 官方 GUID
    private static readonly Guid GUID_SCHEME_HIGH = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid GUID_SUB_PROCESS = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid GUID_PARKING_MIN = new("0cc5b647-c1df-4642-a1c1-d13a4d1b1c5b");
    // 游戏电源档（多核低频平台收益明显：提升全核睿频与升频速度）
    private static readonly Guid GUID_PERFBOOSTMODE = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid GUID_PROCTHROTTLEMIN = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid GUID_PROCTHROTTLEMAX = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid GUID_PERFINCTHRESHOLD = new("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
    private static readonly Guid GUID_PERFINCTIME = new("984cf492-3bed-4488-a8f9-4286c97bf5aa");
    // 电源基线（按方案分别记录；退出/离开游戏时逐一还原）
    private static Guid _originalScheme;
    private static bool _baselineCaptured;
    private static readonly Dictionary<Guid, uint> _parkingBaseline = new();
    // 其他电源设置的原值（方案, 设置项, 原值），离开游戏/退出时逐项还原
    private static readonly List<(Guid scheme, Guid setting, uint value)> _settingBaseline = new();
    private static readonly object _powerLock = new();
    private static bool _powerWarned;
    // 黑屏修复: 幂等记账——已写过的停泊值/游戏电源档/是否有脏设置
    private static readonly Dictionary<Guid, int> _parkingWritten = new();
    private static string _lastGamingProfile = "";
    private static bool _settingsDirty;
    public static void SetCoreParkingPercent(int percent)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return;
        lock (_powerLock)
        {
            // 原来只写“高性能方案”的值，活动方案是平衡时完全不生效
            var active = GetActiveScheme();
            if (active == Guid.Empty) return;
            EnsureParkingBaselineLocked(active);
            int target = Math.Clamp(percent, 0, 100);
            // 黑屏修复: 值没变就不要再“写 + 重新激活方案”。
            // 实测日志里短短 9 秒内方案被重新激活 3 次（21:13:00/04/09），
            // 每次重激活都会把 CPU 电源状态与 DXGI 提交节奏打断一下，
            // 活动画面上就是一闪/一黑。待命↔活跃/载入阶段来回切正是这个路径。
            if (_parkingWritten.TryGetValue(active, out var written) && written == target) return;
            var s = active; var sub = GUID_SUB_PROCESS; var set = GUID_PARKING_MIN;
            uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, (uint)target);
            if (rc != 0) { WarnPowerOnceLocked($"核心停泊写入失败 HRESULT=0x{rc:X8}"); return; }
            _parkingWritten[active] = target;
            _settingsDirty = true;
            rc = PowerSetActiveScheme(IntPtr.Zero, ref s);   // 写入后需重新激活才生效
            if (rc != 0) WarnPowerOnceLocked($"电源方案激活失败 HRESULT=0x{rc:X8}");
        }
    }
    public static bool ApplyHighPerformance()
    {
        lock (_powerLock)
        {
            // 先把“当前方案”的停泊值记下来，离开高性能时才能还原
            if (_originalScheme == Guid.Empty) _originalScheme = GetActiveScheme();
            EnsureParkingBaselineLocked(GetActiveScheme());
            var s = GUID_SCHEME_HIGH;
            uint rc = PowerSetActiveScheme(IntPtr.Zero, ref s);
            if (rc == 0) return true;

            // 部分 OEM/精简系统没有内置“高性能”方案，
            // 此时复制当前方案生成专用方案，保证锁频真的能生效。
            var boost = EnsureBoostSchemeLocked();
            if (boost != Guid.Empty)
            {
                var b = boost;
                if (PowerSetActiveScheme(IntPtr.Zero, ref b) == 0) return true;
            }
            WarnPowerOnceLocked($"高性能方案切换失败 HRESULT=0x{rc:X8}");
            return false;
        }
    }

    private static Guid _boostScheme;
    private static bool _boostTried;

    /// <summary>复制当前方案作为“PCBooster 高性能”（仅在没有标准高性能方案时使用）</summary>
    private static Guid EnsureBoostSchemeLocked()
    {
        if (_boostTried) return _boostScheme;
        _boostTried = true;
        try
        {
            var src = GetActiveScheme();
            if (src == Guid.Empty) return Guid.Empty;
            if (PowerDuplicateScheme(IntPtr.Zero, ref src, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                _boostScheme = Marshal.PtrToStructure<Guid>(p);
                LocalFree(p);
                AppLogger.Info($"已创建专用高性能方案 {_boostScheme}");
            }
        }
        catch (Exception ex) { AppLogger.Warning($"创建高性能方案失败: {ex.Message}"); }
        return _boostScheme;
    }
    /// <summary>游戏电源档——Boost=aggressive、最小/最大处理器状态=100%、
    /// 升频阈值 10 / 升频步进 1（低主频多核平台可减少“卡在低频”的时间）。
    /// 仅在接通电源时启用；所有原值记录后由 RestoreOriginalScheme 还原。</summary>
    public static void ApplyGamingPowerProfile()
    {
        if (!IsOnAcPower()) return;
        lock (_powerLock)
        {
            var active = GetActiveScheme();
            if (active == Guid.Empty) return;
            // 黑屏修复: 同一个方案上重复写 5 项 + 重激活是纯振荡源。
            // 已写过就跳过（退出/恢复时会把指纹清空，下次进游戏会重写）。
            string fingerprint = active.ToString() + "|ac";
            if (_lastGamingProfile == fingerprint) return;
            EnsureParkingBaselineLocked(active);
            WriteSettingLocked(active, GUID_PERFBOOSTMODE, 2);
            WriteSettingLocked(active, GUID_PROCTHROTTLEMIN, 100);
            WriteSettingLocked(active, GUID_PROCTHROTTLEMAX, 100);
            WriteSettingLocked(active, GUID_PERFINCTHRESHOLD, 10);
            WriteSettingLocked(active, GUID_PERFINCTIME, 1);
            var s = active;
            PowerSetActiveScheme(IntPtr.Zero, ref s);
            _lastGamingProfile = fingerprint;
            AppLogger.Info("🔌 游戏电源档: Boost=aggressive / 处理器 100% / 升频阈值 10");
        }
    }
    private static void WriteSettingLocked(Guid scheme, Guid setting, uint value)
    {
        var s = scheme; var sub = GUID_SUB_PROCESS; var st = setting;
        if (!_settingBaseline.Any(e => e.scheme == scheme && e.setting == setting) &&
            PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out var old) == 0)
            _settingBaseline.Add((scheme, setting, old));
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, value);
        if (rc != 0) WarnPowerOnceLocked($"电源设置写入失败({setting.ToString()[..8]}) HRESULT=0x{rc:X8}");
    }
    /// <summary>是否接通电源（电池供电时不启用激进睿频）</summary>
    public static bool IsOnAcPower()
    {
        try
        {
            if (GetSystemPowerStatus(out var s)) return s.ACLineStatus != 0;
        }
        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        // 查询失败时按“电池”处理（保守），
        // 否则会在笔记本上写激进电源档（正是 P0-2 永久副作用的入口）
        return false;
    }
    /// <summary>读取当前活动电源方案（失败返回空 Guid）</summary>
    public static Guid GetActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                var g = Marshal.PtrToStructure<Guid>(p);
                LocalFree(p);
                return g;
            }
        }
        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        return Guid.Empty;
    }
    /// <summary>已为人识别的活动电源方案名称（供 UI 直接展示）</summary>
    public static string GetActiveSchemeName()
    {
        try
        {
            Guid g = GetActiveScheme();
            if (g == Guid.Empty) return "未知";
            if (g == GUID_SCHEME_HIGH) return "高性能";
            if (g == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")) return "节能";
            if (g == new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")) return "平衡";
            return "自定义方案";
        }
        catch { return "未知"; }
    }

    /// <summary>首次改动某方案前记录它的核心停泊原值</summary>
    private static void EnsureParkingBaselineLocked(Guid scheme)
    {
        if (scheme == Guid.Empty || _parkingBaseline.ContainsKey(scheme)) return;
        var s = scheme; var sub = GUID_SUB_PROCESS; var set = GUID_PARKING_MIN;
        if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out var v) != 0) return;
        _parkingBaseline[scheme] = v;
        if (_originalScheme == Guid.Empty) _originalScheme = scheme;
        _baselineCaptured = true;
        AppLogger.Info($"🔌 电源基线已记录（停泊={v}%）");
    }
    /// <summary>把当前方案的核心停泊值恢复为“原始基线”。
    /// 待命/深睡结束回到活跃状态时用，避免固定写 100% 反而偏离用户原策略。</summary>
    public static void RestoreParkingBaseline()
    {
        lock (_powerLock)
        {
            var active = GetActiveScheme();
            if (active == Guid.Empty) return;
            if (!_parkingBaseline.TryGetValue(active, out var v)) return;
            // 没改过 / 已经是基线值 → 不再写+重激活
            if (!_parkingWritten.TryGetValue(active, out var written) || written == v) return;
            var s = active; var sub = GUID_SUB_PROCESS; var set = GUID_PARKING_MIN;
            uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, v);
            if (rc != 0) { WarnPowerOnceLocked($"核心停泊还原失败 HRESULT=0x{rc:X8}"); return; }
            _parkingWritten[active] = (int)v;
            PowerSetActiveScheme(IntPtr.Zero, ref s);
        }
    }
    /// <summary>只恢复电源方案与停泊值，保留基线（供下次场景锁定复用）。
    /// 离开游戏场景时调用，否则机器会一直停留在“高性能”方案直到退出程序。</summary>
    public static void RestoreOriginalScheme()
    {
        lock (_powerLock) RestorePowerLocked(keepBaseline: true);
    }
    /// <summary>退出还原——所有被改过的方案写回原停泊值 + 恢复原电源方案</summary>
    public static void RestorePowerSettings()
    {
        lock (_powerLock) RestorePowerLocked(keepBaseline: false);
    }
    private static void RestorePowerLocked(bool keepBaseline)
    {
        try
        {
            // 原方案还原不能再被“基线未捕获”拦住（停泊值读不到时以前就直接 return，
            // 导致程序退出后系统永远停在高性能方案）。
            if (_originalScheme != Guid.Empty)
            {
                // 已经是原方案就不要重复激活
                if (GetActiveScheme() != _originalScheme)
                {
                    var s = _originalScheme;
                    uint rc = PowerSetActiveScheme(IntPtr.Zero, ref s);
                    if (rc != 0) WarnPowerOnceLocked($"恢复原电源方案失败 HRESULT=0x{rc:X8}");
                }
            }
            if (!_baselineCaptured) return;
            // 黑屏/卡顿修复: 没有脏设置就不重复写 10+ 项电源项。
            // 原来每次“离开游戏 + 待命/唤醒”都会全量重写并重激活一次。
            if (!_settingsDirty)
            {
                if (!keepBaseline) { _parkingBaseline.Clear(); _settingBaseline.Clear(); _baselineCaptured = false; }
                return;
            }
            // 游戏电源档（睿频/处理器最小最大状态/升频阈值）以前只记不还，
            // 退出游戏后用户方案被永久改成激进值（发热/耗电/续航下降）。
            foreach (var item in _settingBaseline)
            {
                var ss = item.scheme; var ssub = GUID_SUB_PROCESS; var sset = item.setting;
                PowerWriteACValueIndex(IntPtr.Zero, ref ss, ref ssub, ref sset, item.value);
            }
            foreach (var kv in _parkingBaseline)
            {
                var s = kv.Key; var sub = GUID_SUB_PROCESS; var set = GUID_PARKING_MIN;
                PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, kv.Value);
                _parkingWritten[s] = (int)kv.Value;
            }
            _settingsDirty = false;
            _lastGamingProfile = "";
            if (!keepBaseline) { _parkingBaseline.Clear(); _settingBaseline.Clear(); _baselineCaptured = false; }
            AppLogger.Info(keepBaseline ? "🔌 电源方案已恢复（保留基线）" : "🔌 电源设置已还原");
        }
        catch (Exception ex) { AppLogger.Error($"电源还原失败: {ex.Message}"); }
    }
    /// <summary>同类电源失败只提示一次，避免每次心跳刷日志</summary>
    private static void WarnPowerOnceLocked(string message)
    {
        if (_powerWarned) return;
        _powerWarned = true;
        AppLogger.Warning($"🔌 {message}（多为权限不足，后续同类失败不再提示）");
    }
    public static bool PreloadProcess(int pid)
    {
        return ProcessSafeHelper.WithProcess(pid, p =>
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) return false;
                // 只触碰前 8MB（原 100MB，磁盘代价远大于收益）+ 顺序扫描提示
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                long budget = Constants.PRELOAD_FILE_LIMIT_MB * 1024L * 1024;
                long total = 0; int read;
                while (total < budget && (read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    total += read;
                return true;
            }
            catch { return false; }
        }, false);
    }
    /// <summary>③根治: 只抬驻留下限，max=-1 永不设天花板</summary>
    public static bool LockProcessMemory(int pid)
    {
        return ProcessSafeHelper.WithProcess(pid, p =>
        {
            try
            {
                var ws = p.WorkingSet64;
                return SetProcessWorkingSetSizeEx(p.Handle, new IntPtr(ws), new IntPtr(-1), 0x00000008);
            }
            catch { return false; }
        }, false);
    }
    /// <summary>释放工作集下限（恢复系统默认 min/max）
    /// 旧版只有 LockProcessMemory，关掉“工作集下限锁定”后已锁进程的下限会留到进程退出。</summary>
    public static bool ReleaseWorkingSetFloor(int pid)
    {
        return ProcessSafeHelper.WithProcess(pid, p =>
        {
            try { return SetProcessWorkingSetSizeEx(p.Handle, new IntPtr(-1), new IntPtr(-1), 0x00000008); }
            catch { return false; }
        }, false);
    }

    /// <summary>温和修剪后立即解除天花板</summary>
    public static bool TrimWorkingSet(int pid, long targetBytes)
    {
        return ProcessSafeHelper.WithProcess(pid, p =>
        {
            try
            {
                var max = new IntPtr(targetBytes);
                var min = new IntPtr(Math.Max(1, targetBytes / 2));
                if (!SetProcessWorkingSetSizeEx(p.Handle, min, max, 0x00000008)) return false;
                SetProcessWorkingSetSizeEx(p.Handle, new IntPtr(1), new IntPtr(-1), 0x00000008);
                return true;
            }
            catch { return false; }
        }, false);
    }
    public static bool UnlockProcessMemory(int pid)
    {
        return ProcessSafeHelper.WithProcess(pid, p =>
        {
            try { return EmptyWorkingSet(p.Handle); }
            catch { return false; }
        }, false);
    }
}
/// <summary>ProcessHelper 别名层（转发后台缓存）</summary>
public static class ProcessHelper
{
    public static double GetProcessCpuUsage(int pid) => NativeApi.GetProcessCpuCached(pid);
    public static int GetForegroundPid() => NativeApi.GetForegroundPid();
}
}
