// ============================================================
// PC Booster v3.6.8
// Intelligence/LearningData.cs
// 统一持久化数据结构（修复 C1 类型定义 / C5 全 public / S2 DTO）
// ============================================================

using PCBooster.Infrastructure.Common;

namespace PCBooster.Intelligence
{
    // ═══════════ 类型定义（C1 集中定义）═══════════

    /// <summary>进程分类枚举</summary>
    public enum ProcessClass
    { Unknown, HeavyCompute, HeavyMemory, Interactive, Background, Burst, System }

    /// <summary>调度策略</summary>
    public class Strategy
    {
        public int ProtectionSeconds { get; set; } = 30;
        public bool CanReclaim { get; set; } = true;
        public bool CanPreload { get; set; } = false;
        public int PreloadPriority { get; set; } = 0;
        public int JobCpuLimit { get; set; } = -1;
        /// <summary>★ 3.6.8: 默认 -1 = 不施加提交帽。
        /// Job 的提交内存上限会让“已超限的进程”后续分配失败 → 浏览器/大型软件直接崩溃，
        /// 因此该能力只允许显式规则开启，不能作为类别的默认值。</summary>
        public int JobMemoryLimitMB { get; set; } = -1;
        public string IoPriority { get; set; } = "Low";
        public bool BoostPriority { get; set; } = false;
        public bool CoreIsolation { get; set; } = false;
        public bool LockFrequency { get; set; } = false;
        /// <summary>★ 3.6.8: 原名为 EnableLargePages，实际作用是“工作集下限锁定”
        ///（大页内存必须由目标进程自己申请，第三方无法代劳）</summary>
        public bool EnableWorkingSetLock { get; set; } = false;
        public bool CacheAware { get; set; } = false;
    }

    /// <summary>周期模式</summary>
    public class PeriodicPattern
    {
        public string ProcessName { get; set; } = "";
        /// <summary>★ 3.6.8: -1 = 任意星期（原默认 0 等同于“周日”，语义错误）</summary>
        public int DayOfWeek { get; set; } = -1;
        public int Hour { get; set; }
        public int Occurrences { get; set; }
        public double Confidence { get; set; }
    }

    // ═══════════ 序列化 DTO（S2 仅值类型）═══════════

    /// <summary>进程数据 DTO（可安全 JSON 序列化）</summary>
    public class ProcessDataDto
    {
        public string Name { get; set; } = "";
        // ★ 3.6.8 性能: 持久化时降采样（每 6 个取 1），仍保留统计特征，重启后无需预热
        public double[] CpuSamples { get; set; } = Array.Empty<double>();
        public long[] WorkingSetSamples { get; set; } = Array.Empty<long>();
        public int ForegroundCount { get; set; }
        public int TotalSamples { get; set; }
        public int TotalLaunches { get; set; }
        public double TotalRunMinutes { get; set; }
        public int[] HourlyLaunches { get; set; } = new int[96];
        /// <summary>★ 3.6.8: 使用维度（启动 + 前台切换），预测主信号</summary>
        public int[] HourlyUsage { get; set; } = new int[96];
        public int[] WeeklyUsage { get; set; } = new int[7];
        public DateTime LastForegroundAt { get; set; } = DateTime.MinValue;
        public string UserOverride { get; set; } = "";
        public DateTime FirstSeen { get; set; } = DateTime.Now;
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    // ═══════════ 统一学习数据（单文件）═══════════

    /// <summary>统一学习数据（替代 3 个旧文件）</summary>
    public class LearningData
    {
        public Dictionary<string, ProcessDataDto> Processes { get; set; } = new();
        public Dictionary<string, FeedbackLoop.ConfidenceRecord> Confidences { get; set; } = new();
        public List<PeriodicPattern> PeriodicPatterns { get; set; } = new();
        public string CurrentScene { get; set; } = Constants.SCENE_GENERAL;
        public int TotalHits { get; set; }
        public int TotalMisses { get; set; }
        public DateTime LastSave { get; set; } = DateTime.Now;
    }
}