// ============================================================
// PC Booster v3.6.9
// Infrastructure/Profiling/CpuTopologyEx.cs
// R17: L3 域（AMD CCD / Intel ring）+ 混合架构（P/E 核）感知的掩码计算
//   借鉴自 FlexCore 的「按 CCD / 性能核选择核心」思路，但只做掩码决策，
//   不引入全局调度接管（避免与其它调度工具互抢）。
//   · 本机 Xeon E5 v3：16C/32T，L3 分成 2 个 20MB 环域 —— 把游戏放在同一个环内，
//     可避免跨环 L3 访问与内存延迟惩罚。
//   · 混合架构（Intel 12 代+）：游戏核优先效率等级 0（P 核），后台核优先 E 核。
// ============================================================
using System.Runtime.InteropServices;
using PCBooster.Infrastructure.Common;
namespace PCBooster.Infrastructure.Profiling
{
/// <summary>扩展 CPU 拓扑（L3 域 / 能效等级）</summary>
public static class CpuTopologyEx
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int CacheLevelL3 = 3;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType,
        IntPtr buffer, ref int returnedLength);
    private sealed class PhysicalCore
    {
        public ulong LogicalMask;     // 含 SMT 兄弟线程
        public int EfficiencyClass;   // 0 = P 核；>0 = E 核（非混合架构恒为 0）
        public int L3Domain = -1;
    }
    private sealed class L3Domain
    {
        public ulong Mask;
        public long Bytes;
    }
    private static readonly List<PhysicalCore> _cores = new();
    private static readonly List<L3Domain> _domains = new();
    private static readonly object _lock = new();
    private static bool _detected;
    private static int _minEffClass;
    private static int _maxEffClass;
    /// <summary>是否已完成扩展拓扑检测</summary>
    public static bool IsReady { get { lock (_lock) return _detected && _cores.Count > 0; } }
    /// <summary>物理核数（Win32 API 权威值）</summary>
    public static int CoreCount { get { lock (_lock) return _cores.Count; } }
    /// <summary>L3 域数量（真实 CCD/环；Windows 对多环至强常报成 1）</summary>
    public static int L3DomainCount { get { lock (_lock) return _domains.Count; } }
    private static int _logicalCount;
    /// <summary>逻辑核数（含 SMT 兄弟线程）</summary>
    public static int LogicalCount { get { lock (_lock) return _logicalCount; } }
    /// <summary>检测一次（失败时保持未就绪，调用方回退旧逻辑）</summary>
    public static void Detect()
    {
        lock (_lock)
        {
            if (_detected) return;
            _detected = true;
            try
            {
                ReadCores();
                ReadL3Domains();
                MapCoresToDomains();
                if (_cores.Count > 0)
                {
                    _minEffClass = _cores.Min(c => c.EfficiencyClass);
                    _maxEffClass = _cores.Max(c => c.EfficiencyClass);
                    _logicalCount = _cores.Sum(c => BitCount(c.LogicalMask));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"扩展拓扑检测失败: {ex.Message}"); }
        }
    }
    private static void ReadCores()
    {
        int len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
        if (len <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return;
            int offset = 0;
            while (offset + 8 <= len)
            {
                int relationship = Marshal.ReadInt32(buf, offset);
                int size = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore)
                {
                    int effClass = Marshal.ReadByte(buf, offset + 9);      // Flags 之后
                    int groupCount = Marshal.ReadInt16(buf, offset + 30);
                    ulong mask = 0;
                    for (int g = 0; g < Math.Max(1, groupCount); g++)
                    {
                        int p = offset + 32 + g * 16;                      // GROUP_AFFINITY
                        ulong m = unchecked((ulong)Marshal.ReadInt64(buf, p));
                        if (g == 0) mask |= m;                             // 单组（≤64 逻辑核）足够
                    }
                    if (mask != 0) _cores.Add(new PhysicalCore { LogicalMask = mask, EfficiencyClass = effClass });
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    private static void ReadL3Domains()
    {
        int len = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref len);
        if (len <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buf, ref len)) return;
            int offset = 0;
            while (offset + 8 <= len)
            {
                int relationship = Marshal.ReadInt32(buf, offset);
                int size = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationCache)
                {
                    int level = Marshal.ReadByte(buf, offset + 8);
                    int cacheSize = Marshal.ReadInt32(buf, offset + 12);
                    int groupCount = Marshal.ReadInt16(buf, offset + 38);
                    if (level == CacheLevelL3 && cacheSize > 0)
                    {
                        ulong mask = 0;
                        for (int g = 0; g < Math.Max(1, groupCount); g++)
                        {
                            int p = offset + 40 + g * 16;
                            ulong m = unchecked((ulong)Marshal.ReadInt64(buf, p));
                            if (g == 0) mask |= m;
                        }
                        if (mask != 0) _domains.Add(new L3Domain { Mask = mask, Bytes = cacheSize });
                    }
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    private static void MapCoresToDomains()
    {
        for (int i = 0; i < _cores.Count; i++)
            for (int d = 0; d < _domains.Count; d++)
                if ((_cores[i].LogicalMask & _domains[d].Mask) != 0) { _cores[i].L3Domain = d; break; }
    }
    /// <summary>游戏/重载掩码：优先同一 L3 域（CCD/环），混合架构优先 P 核
    /// ★ 单域但核数多的平台（如至强双环）可传 singleRingOnly=true，把游戏核限制在半个核数内</summary>
    public static ulong GetGameMask(int gameCores, bool singleRingOnly = false)
    {
        lock (_lock)
        {
            if (!_detected || _cores.Count == 0) return 0;
            // 留至少 1 个物理核给后台，否则后台掩码会变成 0（隔离反而失效）
            int cap = Math.Max(1, _cores.Count - 1);
            int need = Math.Clamp(gameCores, 1, cap);
            // ★ 可选：单 L3 域平台按“半个核数”当一簇（用户显式开启才生效）
            if (singleRingOnly && _domains.Count <= 1 && _cores.Count >= 8)
                need = Math.Min(need, Math.Max(1, _cores.Count / 2));
            var pool = HasHybrid
                ? _cores.Where(c => c.EfficiencyClass == _minEffClass).ToList()
                : _cores;
            if (pool.Count == 0) pool = _cores;
            // ① 选域：优先能装下 need 个核、且缓存最大的域
            int bestDomain = -1;
            if (!HasHybrid && _domains.Count > 1)
            {
                for (int d = 0; d < _domains.Count; d++)
                {
                    int inside = pool.Count(c => c.L3Domain == d);
                    if (inside >= need && (bestDomain < 0 || _domains[d].Bytes > _domains[bestDomain].Bytes))
                        bestDomain = d;
                }
            }
            var picked = new List<PhysicalCore>();
            if (bestDomain >= 0)
                picked.AddRange(pool.Where(c => c.L3Domain == bestDomain).OrderBy(c => c.LogicalMask).Take(need));
            // ② 域内不够（或不分域）：按编号补齐
            if (picked.Count < need)
                foreach (var c in pool.OrderBy(c => c.LogicalMask))
                {
                    if (picked.Count >= need) break;
                    if (!picked.Contains(c)) picked.Add(c);
                }
            ulong mask = 0;
            foreach (var c in picked) mask |= c.LogicalMask;
            return mask;
        }
    }
    /// <summary>后台掩码：混合架构优先 E 核；否则取游戏核之外的其余逻辑核</summary>
    public static ulong GetBackgroundMask(int gameCores, bool singleRingOnly = false)
    {
        lock (_lock)
        {
            if (!_detected || _cores.Count == 0) return 0;
            ulong game = GetGameMask(gameCores, singleRingOnly);
            if (HasHybrid)
            {
                ulong eMask = 0;
                foreach (var c in _cores.Where(c => c.EfficiencyClass > _minEffClass)) eMask |= c.LogicalMask;
                if (eMask != 0 && (eMask & ~game) != 0) return eMask & ~game;
            }
            ulong all = 0;
            foreach (var c in _cores) all |= c.LogicalMask;
            return all & ~game;
        }
    }
    private static bool HasHybrid { get { return _maxEffClass > _minEffClass; } }
    /// <summary>可读描述（启动日志 / 设置页提示）</summary>
    public static string Describe()
    {
        lock (_lock)
        {
            if (!_detected || _cores.Count == 0) return "扩展拓扑不可用（回退基础拓扑）";
            int logical = _cores.Sum(c => BitCount(c.LogicalMask));
            var sb = new System.Text.StringBuilder();
            sb.Append($"{_cores.Count}C/{logical}T");
            if (_domains.Count > 1)
            {
                var sizes = _domains.Select(d => d.Bytes / 1024 / 1024).OrderByDescending(x => x);
                sb.Append($" · L3域 {_domains.Count}×{string.Join("/", sizes)}MB");
            }
            else if (_domains.Count == 1) sb.Append($" · L3 {_domains[0].Bytes / 1024 / 1024}MB");
            sb.Append(HasHybrid ? $" · 混合架构 P/E（{_cores.Count(c => c.EfficiencyClass == _minEffClass)}P/{_cores.Count(c => c.EfficiencyClass > _minEffClass)}E）" : " · 同构核心");
            return sb.ToString();
        }
    }
    private static int BitCount(ulong v)
    {
        int n = 0;
        while (v != 0) { v &= v - 1; n++; }
        return n;
    }
}
}
