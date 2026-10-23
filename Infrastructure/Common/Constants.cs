// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/Constants.cs
// 不可变系统常量 + 3.6.7 硬编码治理具名化（单一事实源）
// ============================================================
namespace PCBooster.Infrastructure.Common
{
/// <summary>不可变系统常量</summary>
public static class Constants
{
// ═══════════ 基础 ═══════════
public const string VERSION = "3.9.8";
public const string MUTEX_NAME = @"Local\PCBooster_SingleInstance";
    public const string FILE_CONFIG = "config.ini";
    public const string FILE_RULES = "rules.json";
    public const string FILE_LEARNING = "learning_data.json";
    public const string DIR_LOGS = "logs";
    // ═══════════ 日志 ═══════════
    public const int MAX_LOG_QUEUE_SIZE = 1000;
    public const int LOG_BATCH_SIZE = 20;
    public const int LOG_RETENTION_DAYS = 7;
    public const int LOG_DROP_ALERT_THRESHOLD = 100;
    // ═══════════ 双速心跳 ═══════════
    public const int FAST_HEARTBEAT_MS = 2000;
    public const int SLOW_HEARTBEAT_MS = 10000;
    // ═══════════ 缓存 ═══════════
    public const double PREDICTION_CACHE_SECONDS = 5.0;
    public const double SNAPSHOT_CACHE_SECONDS = 3.0;   // 2s→3s，降低自身全进程枚举开销
    public const double LOAD_CACHE_SECONDS = 2.0;
    // ═══════════ 负载分级 ═══════════
    public const double CPU_IDLE_THRESHOLD = 20;
    public const double CPU_LIGHT_THRESHOLD = 40;
    public const double CPU_MODERATE_THRESHOLD = 60;
    /// <summary>负载分级滞回（百分点），避免阈值附近每 2 秒抖动</summary>
    public const double LOAD_LEVEL_HYSTERESIS = 5;
    // ═══════════ 内存回收 ═══════════
    public const double MEMORY_RECLAIM_THRESHOLD = 60;
    // ═══════════ 预加载 ═══════════
    public const int PRELOAD_MAX_CONCURRENT = 3;
    public const double PRELOAD_MIN_THRESHOLD = 0.30;
    public const double PRELOAD_MAX_THRESHOLD = 0.75;
    // 自适应阈值（命中率低→升、高→降）——替代原“只升不降”的棘轮
    public const double THRESHOLD_STEP = 0.01;
    public const int FEEDBACK_WINDOW_SAMPLES = 40;
    public const double FEEDBACK_LOW_HIT_RATE = 0.40;
    public const double FEEDBACK_HIGH_HIT_RATE = 0.60;
    public const long PRELOAD_SKIP_THRESHOLD_MB = 512;
    public const long PRELOAD_SKIP_THRESHOLD_GAME_MB = 2048;
    public const int PRELOAD_FILE_LIMIT_MB = 8;
    // ═══════════ 保护 ═══════════
    public const int PROTECTION_MIN_SECONDS = 30;
    public const int PROTECTION_MAX_SECONDS = 300;
    // ═══════════ 模块健康 ═══════════
    public const int MODULE_DEGRADED_THRESHOLD = 3;
    public const int MODULE_ISOLATED_THRESHOLD = 5;
    public const int MODULE_RECOVERY_INTERVAL_SECONDS = 60;
    // ═══════════ 决策必要性 ═══════════
    public const double NECESSITY_BOOST_STEP = 0.1;
    public const double NECESSITY_DECAY_FACTOR = 0.9;
    public const double NECESSITY_SKIP_THRESHOLD = 0.15;
    /// <summary>必要性分数回收间隔（防止“一次表现差 → 永久停用”）</summary>
    public const int NECESSITY_RECOVERY_INTERVAL_SECONDS = 900;
    /// <summary>每次回收向 0.5 拉回的比例（给被停用的决策重新试错机会）</summary>
    public const double NECESSITY_RECOVERY_PULL = 0.2;
    // ═══════════ 置信度 ═══════════
    public const double CONFIDENCE_MIN = 0.05;
    public const double CONFIDENCE_MAX = 1.0;
    public const double CONFIDENCE_HIT_BONUS = 0.06;
    public const double CONFIDENCE_MISS_PENALTY = 0.03;   // 原 0.08 使均衡点高达 61.5%，现降为 33%
    public const double CONFIDENCE_DECAY_NORMAL = 0.98;
    public const double CONFIDENCE_DECAY_HIGH_HIT = 0.995;
    public const double CONFIDENCE_DECAY_LOW_HIT = 0.95;
    // ═══════════ 进程画像 ═══════════
    public const int PROFILER_WINDOW_SIZE = 60;
    public const int PROFILER_MIN_SAMPLES = 10;
    public const int PROFILER_STALE_DAYS = 30;
    public const long PROFILER_HEAVY_MEM_THRESHOLD = 1024L * 1024 * 1024;
    public const long PROFILER_MED_MEM_THRESHOLD = 256L * 1024 * 1024;
    public const double PROFILER_HIGH_CPU_THRESHOLD = 50;
    public const double PROFILER_LOW_CPU_THRESHOLD = 10;
    public const double PROFILER_HIGH_VOLATILITY = 25;
    public const double PROFILER_HIGH_FOREGROUND_RATIO = 0.5;
    public const double PROFILER_LOW_FOREGROUND_RATIO = 0.1;
    public const int PROFILER_SAMPLE_EVERY_N_CYCLES = 30;
    // ═══════════ 场景 ═══════════
    public const string SCENE_GAMING = "gaming";
    // 游戏在后台（已启动但前台是其他程序）——保留隔离、释放锁频
    public const string SCENE_GAMING_BACKGROUND = "gaming-bg";
    // 影音场景（只关核心停泊，不锁频）
    public const string SCENE_VIDEO = "video";
    public const string SCENE_DESIGN = "design";
    public const string SCENE_DEVELOPMENT = "development";
    public const string SCENE_MEETING = "meeting";
    public const string SCENE_OFFICE = "office";
    public const string SCENE_BROWSING = "browsing";
    public const string SCENE_GENERAL = "general";
    
    // 核心隔离适用场景（高算力/低延迟敏感：游戏/设计/开发）
    public static readonly HashSet<string> ISOLATION_SCENES = new(StringComparer.OrdinalIgnoreCase)
    {
        SCENE_GAMING, SCENE_GAMING_BACKGROUND, SCENE_DESIGN, SCENE_DEVELOPMENT
    };
    // ═══════════ 电源 ═══════════
    public const int CORE_PARKING_ACTIVE = 100;
    public const int CORE_PARKING_LIGHT_STANDBY = 50;
    public const int CORE_PARKING_DEEP_SLEEP = 20;
    public const double LIGHT_STANDBY_TIMEOUT_SECONDS = 30;
    public const double DEEP_SLEEP_TIMEOUT_SECONDS = 180;
    // 离开游戏的去抖延迟（秒）。全屏游戏在切换显示模式/短暂掉前台时，
    // 场景会在 gaming ↔ gaming-bg 之间抖动；旧版每次抖动都切一次电源方案
    // （高性能↔原方案 + 写 5 个电源项），实测 9 秒内切了 2 轮，
    // 会打断 GPU 提交节奏（日志同时出现 nvlddmkm 153），用户看到的就是“跳闪/黑屏”。
    // 现在离开游戏后先等这个宽限期，期间回到游戏就取消恢复。
    public const int GAMING_EXIT_POWER_RESTORE_DELAY_SECONDS = 20;
    // ═══════════ 黑屏/卡死防护（防电源与亲和性振荡）═══════════
    /// <summary>进入游戏场景后需稳定这么久才真正锁频。
    /// 全屏游戏切换显示模式时 gaming↔gaming-bg 会秒级抖动，抖动期间锁频就是闪屏/黑屏的源头。</summary>
    public const int FREQ_LOCK_STABLE_SECONDS = 3;
    /// <summary>两次“锁频/恢复”之间的最小间隔（任何方向），低于此值的转换被抑制。</summary>
    public const int FREQ_TRANSITION_MIN_INTERVAL_SECONDS = 15;
    /// <summary>同一进程同一掩码在这么久内不重复写亲和性（每次写都会让游戏抖动一下）。</summary>
    public const int AFFINITY_REAPPLY_MIN_INTERVAL_SECONDS = 30;
    /// <summary>后台核心罩全快照扫描的最小间隔（前台事件很多，扫描本身在抢 CPU）。</summary>
    public const int BACKGROUND_ISOLATION_MIN_SCAN_INTERVAL_MS = 5000;
    /// <summary>卡死看门狗：快心跳两次之间超过这么久未执行就记一条“心跳中断”。</summary>
    public const int STALL_WATCHDOG_INTERVAL_MS = 5000;
    public const int STALL_WARN_GAP_SECONDS = 15;
    /// <summary>非游戏场景转换需连续多少次采样一致才真正切换（快心跳 2s/次）。
    /// 用于消除全屏游戏切显示模式时 gaming↔gaming-bg 的秒级闪烁。</summary>
    public const int SCENE_CONFIRM_SAMPLES = 2;
    // ═══════════ 学习 ═══════════
    public const double IDLE_CHECK_INTERVAL_SECONDS = 300;
    public const double IDLE_CPU_THRESHOLD = 15;
    // 系统级活动门控（挂机编译/渲染/下载时不再降核）
    public const double DEEP_SLEEP_CPU_GUARD = 15;
    public const double LEARNING_QUALITY_THRESHOLD = 0.7;
    public const double PREDICTION_WINDOW_SECONDS = 120;   // 30s→120s（切窗口间隔普遍超过 30s）
    // ═══════════ Job ═══════════
    public const double AUTOJOB_MEM_TRIGGER = 40;
    public const int AUTOJOB_MAX_TARGETS = 3;
    public const long AUTOJOB_MIN_WORKING_SET_MB = 256;
    // 单进程“吃满一个核”判据（原整机 70% 在 8 核机上永不触发）
    public const double AUTOJOB_HOG_CPU = 60;
    public const int AUTOJOB_HOG_STREAK = 3;
    /// <summary>刚切到后台不久的进程不施加 Job 限制（避免“切个窗口就被限速/被杀”）</summary>
    public const int AUTOJOB_RECENT_FG_SECONDS = 300;
    // 游戏场景激进档（后台抢资源容忍度更低）
    public const double AUTOJOB_HOG_CPU_GAME = 40;
    public const int AUTOJOB_MAX_TARGETS_GAME = 6;
    // ═══════════ 日常模式保护（WorkloadGuard）═══════════
    /// <summary>编译工具链收尾保护窗口。链接/落盘在进程退出后仍在进行，
    /// 这段时间内既不限速也不修剪，避免“编译完成瞬间卡一下”。</summary>
    public const int WORKGUARD_BUILD_TAIL_SECONDS = 30;
    /// <summary>浏览器多久没在前台才允许修剪它的后台子进程（标签页）</summary>
    public const int WORKGUARD_BROWSER_QUIET_MINUTES = 5;
    /// <summary>办公程序在前台期间的保护窗口（保存/打印/自动保存都发生在这个窗口内）</summary>
    public const int WORKGUARD_OFFICE_PROTECT_MINUTES = 5;
    /// <summary>WorkloadGuard 内部表的容量上限（超限即按存活 PID 清理）</summary>
    public const int WORKGUARD_MAX_ENTRIES = 256;
    // ═══════════ 指令集 ═══════════
    public const double CACHE_MISS_SENSITIVE_THRESHOLD = 30;
    public const double CACHE_CHECK_CACHE_SECONDS = 30;
    public const long LARGE_PAGE_WORKING_SET_THRESHOLD = 1024L * 1024 * 1024;
    // ═══════════ 64位/合规/治理配套 ═══════════
    public const double LAST_INPUT_POLL_SECONDS = 1.0;
    public const double TIME_LEAP_FACTOR = 3.0;
    public const double FLUSH_INTERVAL_SECONDS = 300;
    public const int SHUTDOWN_FLUSH_TIMEOUT_MS = 2000;
    public const int MAX_PROCESSES_TRACKED = 2000;
    public const int CPU_TOP_SAMPLE_COUNT = 128;
    // ═══════════ 3.6.7 硬编码治理（B类具名化）═══════════
    public const int TIME_SLOTS_PER_DAY = 96;
    public const int SEQUENCE_MAX_LENGTH = 4;
    public const int SANITIZE_MAX_LENGTH = 64;
    public const double RUN_MINUTES_PER_TICK = FAST_HEARTBEAT_MS / 60000.0;
    public const double WEIGHT_HOURLY = 0.12;
public const double WEIGHT_WEEKDAY = 0.05;   // 从 HOURLY 分出周维度权重（总分保持 1.0）
    // 新增“前台使用”权重——原来只学启动事件，与用户“切窗口”的主要行为错位
    public const double WEIGHT_FOREGROUND = 0.15;
    /// <summary>前台占比放大系数（占比 50% 即得满分 1.0）</summary>
    public const double FG_SHARE_GAIN = 2.0;
    /// <summary>最近一次前台使用在多长时间内仍享加成（分钟）</summary>
    public const int FG_RECENT_MINUTES = 30;
    public const double WEIGHT_PERIODIC = 0.15;
    public const double WEIGHT_SEQUENCE = 0.15;
    public const double WEIGHT_MARKOV = 0.12;
    public const double WEIGHT_SCENE = 0.10;
    public const double WEIGHT_CONFIDENCE = 0.10;
    public const double WEIGHT_EXPLORATION = 0.06;
    public const double EXPLORATION_BONUS = 0.15;
    public const int EXPLORATION_MIN_LAUNCHES = 5;
    public const double SCENE_GAME_BOOST = 0.5;
    public const int NGRAM_MIN_OCCURRENCES = 2;
    public const int NGRAM_MAX_ENTRIES = 2000;
    public const double PATTERN_CONFIDENCE_CAP = 0.95;
    public const double PATTERN_OCCURRENCE_DIVISOR = 30;
    public const int FEEDBACK_HIGH_HIT_MIN_HITS = 10;
    public const double FEEDBACK_HIGH_HIT_MISS_RATIO = 0.2;
    // FEEDBACK_THRESHOLD_STEP 已移除（阈值改由 THRESHOLD_STEP 双向自适应）
    public const double WAKE_CONFIDENCE_THRESHOLD = 0.75;
    public const double IDLE_MEMORY_THRESHOLD = 30;
    public const double SKIP_HIGH_CONFIDENCE = 0.80;
    public const int COLD_START_MINUTES = 60;   // 改为“有效采集分钟数”（原 COLD_START_SAMPLES=100 实际只扛 2 秒）
    public const double DEGRADE_MIN_CONFIDENCE = 0.2;
    public const double DEGRADE_HEALTHY_RATIO = 0.5;
    public const double DEGRADE_DECENT_CONFIDENCE = 0.3;
    public const int FOREGROUND_THROTTLE_MS = 200;
    public const double PROTECTION_CONFIDENCE_BASE = 0.5;
    public const double CACHE_CLEANUP_INTERVAL_SECONDS = 30;
    public const int GAME_CORE_DIVISOR = 2;
    /// <summary>多核低频（Xeon E5 等）判定阈值（物理核）</summary>
    public const int MANYCORE_PHYSICAL_THRESHOLD = 12;
    /// <summary>多核低频平台的游戏核心数下限/上限（物理核）</summary>
    public const int MANYCORE_GAME_CORES_MIN = 4;
    public const int MANYCORE_GAME_CORES_MAX = 8;
    public const double CACHE_FAULT_SCALE = 100;
    // CACHE_OPTIMAL_CORE_COUNT 已删除——缓存敏感不再“只留 2 个物理核”，
    // 改为“限制在同一 L3 域内”（见 CacheAwareScheduler.GetCacheOptimalMask）。
    public const int LEARN_RULE_MIN_OCCURRENCES = 5;
    public const int LEARN_RULE_PRIORITY = 3;
    public const int MODULE_SLEEP_PRIORITY_LIGHT = 5;
    // 内存回收分档（3.6.7 激进档 v2）
    public const double RECLAIM_GENTLE_PRESSURE = 30;
    public const double RECLAIM_NORMAL_PRESSURE = 22;
    public const double RECLAIM_EMERGENCY_PRESSURE = 12;
    public const double RECLAIM_COLD_MAX_CPU30 = 8;
    public const double RECLAIM_WARM_MAX_CPU30 = 15;
    public const long RECLAIM_BLOAT_WS = 768L * 1024 * 1024;
    public const long RECLAIM_TRIM_FLOOR_MB = 128;
    public const double RECLAIM_RATIO_GENTLE = 0.8;
    public const double RECLAIM_RATIO_NORMAL_COLD = 0.6;
    public const double RECLAIM_RATIO_NORMAL_WARM = 0.8;
    public const double RECLAIM_RATIO_EMERGENCY_COLD = 0.5;
    public const double RECLAIM_RATIO_EMERGENCY_WARM = 0.6;
    public const double RECLAIM_FULL_CLEAR_MIN_WS = 1024L * 1024 * 1024;
    public const int RECLAIM_LIMIT_GENTLE = 3;
    public const int RECLAIM_LIMIT_NORMAL = 5;
    public const int RECLAIM_LIMIT_EMERGENCY = 8;
    public const int RECLAIM_COOLDOWN_SECONDS = 180;
    public const int RECLAIM_COOLDOWN_EMERGENCY_SECONDS = 60;
    public const int RECLAIM_RECENT_FG_SECONDS = 1800;   // 5min→30min，刚用过的程序不再被修剪
    public const int RECLAIM_TRIM_INTERVAL_MS = 30;
    public const double RECLAIM_TRIM_RETENTION_MINUTES = 10;
    // UI
    public const int UI_LOG_MAX_LINES = 500;
    // ═══════════ 3.6.8 缺陷修复补充 ═══════════
    /// <summary>未记录进程的置信度初值（与 ConfidenceRecord 一致）</summary>
    public const double CONFIDENCE_DEFAULT = 0.5;
    /// <summary>自动 Job 解除阈值（带滞回，避免临界抖动）</summary>
    public const double AUTOJOB_RELEASE_CPU = 50;
    public const double AUTOJOB_RELEASE_MEM = 55;
    /// <summary>Job 施加失败同 PID 重试冷却</summary>
    public const int AUTOJOB_FAIL_COOLDOWN_SECONDS = 300;
    /// <summary>策略对象缓存 TTL</summary>
    public const double STRATEGY_CACHE_SECONDS = 30;
    /// <summary>后台隔离每轮最多处理进程数（限制系统调用开销）</summary>
    public const int BACKGROUND_ISOLATION_MAX_PER_CYCLE = 32;
    /// <summary>单帧进程退出事件上限</summary>
    public const int EXIT_EVENT_LIMIT = 20;
    // ═══════════ 3.6.8 预测效率 ═══════════
    /// <summary>预测登记：每轮只登记前 N 条（限制反馈噪声）</summary>
    public const int PENDING_REGISTER_TOP = 3;
    /// <summary>同一进程重复登记的最小间隔（秒）</summary>
    public const int PENDING_REGISTER_COOLDOWN_SECONDS = 180;
    /// <summary>待确认预测表上限</summary>
    public const int PENDING_MAX_ENTRIES = 64;
    /// <summary>周期模式上限（防止无界增长）</summary>
    public const int PATTERN_MAX_ENTRIES = 5000;
}
}
