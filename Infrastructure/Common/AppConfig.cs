// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Infrastructure/Common/AppConfig.cs
// 统一配置（B模式: config.ini 存 %AppData%）
// ============================================================
using System.Globalization;
using System.IO;
using System.Text;
namespace PCBooster.Infrastructure.Common
{
/// <summary>配置键与默认值单一事实源</summary>
public static class ConfigSchema
{
    public const string PreloadThreshold = "PreloadThreshold";
    public const string EnableAutoJob = "EnableAutoJob";
    public const string EnableIdleLearning = "EnableIdleLearning";
    public const string EnableDataCollection = "EnableDataCollection";
    public const string EnableFrequencyLock = "EnableFrequencyLock";    // 旧键名（语义错误）仅用于迁移，不再写入配置
    public const string LegacyEnableLargePages = "EnableLargePages";
    // 语义纠正——所谓“大页”实际是工作集下限锁定（大页必须由目标进程自行申请）
    public const string EnableWorkingSetLock = "EnableWorkingSetLock";
    public const string EnableInstructionSetScheduling = "EnableInstructionSetScheduling";
    /// <summary>手动指定游戏核心数（0=自动；E5 建议 4~6）</summary>
    public const string GameCoreCount = "GameCoreCount";
    // 自动限速/内存阈值的用户可调项
    public const string AutoJobHogCpu = "AutoJobHogCpu";
    public const string AutoJobHogCpuGame = "AutoJobHogCpuGame";
    public const string AutoJobMemTrigger = "AutoJobMemTrigger";
    public const string AutoJobMaxTargets = "AutoJobMaxTargets";
    public const string AutoJobMaxTargetsGame = "AutoJobMaxTargetsGame";
    public const string MemoryReclaimTrigger = "MemoryReclaimTrigger";
    public const string PreloadList = "PreloadList";
    public const string EnableMetadataCache = "EnableMetadataCache";
    // 内存修剪默认关闭（Windows 自带 standby/压缩，第三方 Trim 往往制造二次卡顿）
    public const string EnableMemoryReclaim = "EnableMemoryReclaim";
    public const string EnableTrayResident = "EnableTrayResident";
    public const string LaunchAtStartup = "LaunchAtStartup";
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [PreloadThreshold] = "0.35",
        [EnableAutoJob] = "True",
        [EnableIdleLearning] = "True",
        [EnableDataCollection] = "False",
        [EnableMemoryReclaim] = "False",
        [EnableFrequencyLock] = "True",
        [EnableWorkingSetLock] = "False",
        [EnableInstructionSetScheduling] = "True",
        [GameCoreCount] = "0",
        [AutoJobHogCpu] = "60",
        [AutoJobHogCpuGame] = "40",
        [AutoJobMemTrigger] = "40",
        [AutoJobMaxTargets] = "3",
        [AutoJobMaxTargetsGame] = "6",
        [MemoryReclaimTrigger] = "60",
        [PreloadList] = "",
        [EnableMetadataCache] = "True",
        [EnableTrayResident] = "True",
        [LaunchAtStartup] = "False",
    };
}
/// <summary>统一配置管理器（唯一事实源）</summary>
public static class AppConfig
{
private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
private static readonly object _lock = new();
    public static void Load()
    {
        SetDefaults();
        if (File.Exists(AppPaths.ConfigFile))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(AppPaths.ConfigFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int i = line.IndexOf('=');
                    if (i > 0) lock (_lock) _values[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
            }
            catch (Exception ex) { AppLogger.Error($"配置加载失败: {ex.Message}"); }
        }
        else ForceSave();
        // 旧键迁移——“大页”实为工作集下限锁定，旧配置不能丢
        if (GetBool(ConfigSchema.LegacyEnableLargePages, false) &&
            !GetBool(ConfigSchema.EnableWorkingSetLock, false))
            Set(ConfigSchema.EnableWorkingSetLock, "True");
        lock (_lock) _values.Remove(ConfigSchema.LegacyEnableLargePages);   // 迁移后不再写回
        Validate();
    }
    /// <summary>原子写保存</summary>
    public static bool ForceSave()
    {
        var sb = new StringBuilder();
        sb.AppendLine("; PC Booster 配置（自动生成）");
        lock (_lock) foreach (var kv in _values) sb.AppendLine($"{kv.Key}={kv.Value}");
        return AtomicFile.Write(AppPaths.ConfigFile, sb.ToString());
    }
    public static string Get(string key, string def = "")
    { lock (_lock) return _values.TryGetValue(key, out var v) ? v : def; }
    public static void Set(string key, string value)
    { lock (_lock) _values[key] = value; }
    public static bool GetBool(string key, bool def = false)
        => bool.TryParse(Get(key, def.ToString()), out var r) ? r : def;
    public static double GetDouble(string key, double def = 0)
        => double.TryParse(Get(key, def.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
    // 默认值统一取自 ConfigSchema（消除双事实源）
    private static bool SchemaBool(string key)
        => ConfigSchema.Defaults.TryGetValue(key, out var v) && bool.TryParse(v, out var r) && r;
    private static double SchemaDouble(string key)
        => ConfigSchema.Defaults.TryGetValue(key, out var v) &&
           double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0;
    public static double PreloadThreshold
    {
        get => GetDouble(ConfigSchema.PreloadThreshold, SchemaDouble(ConfigSchema.PreloadThreshold));
        set => Set(ConfigSchema.PreloadThreshold, value.ToString("F2", CultureInfo.InvariantCulture));
    }
    public static bool EnableAutoJob { get => GetBool(ConfigSchema.EnableAutoJob, SchemaBool(ConfigSchema.EnableAutoJob)); set => Set(ConfigSchema.EnableAutoJob, value.ToString()); }
    public static bool EnableIdleLearning { get => GetBool(ConfigSchema.EnableIdleLearning, SchemaBool(ConfigSchema.EnableIdleLearning)); set => Set(ConfigSchema.EnableIdleLearning, value.ToString()); }
    public static bool EnableDataCollection { get => GetBool(ConfigSchema.EnableDataCollection, SchemaBool(ConfigSchema.EnableDataCollection)); set => Set(ConfigSchema.EnableDataCollection, value.ToString()); }
    /// <summary>内存修剪总开关（默认关）</summary>
    public static bool EnableMemoryReclaim { get => GetBool(ConfigSchema.EnableMemoryReclaim, SchemaBool(ConfigSchema.EnableMemoryReclaim)); set => Set(ConfigSchema.EnableMemoryReclaim, value.ToString()); }
    /// <summary>工作集下限锁定（需管理员；与“大页”无关）</summary>
    public static bool EnableWorkingSetLock { get => GetBool(ConfigSchema.EnableWorkingSetLock, SchemaBool(ConfigSchema.EnableWorkingSetLock)); set => Set(ConfigSchema.EnableWorkingSetLock, value.ToString()); }
    public static bool EnableTrayResident { get => GetBool(ConfigSchema.EnableTrayResident, SchemaBool(ConfigSchema.EnableTrayResident)); set => Set(ConfigSchema.EnableTrayResident, value.ToString()); }
    public static bool LaunchAtStartup { get => GetBool(ConfigSchema.LaunchAtStartup, SchemaBool(ConfigSchema.LaunchAtStartup)); set => Set(ConfigSchema.LaunchAtStartup, value.ToString()); }
    private static void SetDefaults()
    { foreach (var kv in ConfigSchema.Defaults) Set(kv.Key, kv.Value); }
    /// <summary>钳制范围改为常量单一事实源（原硬编码 0.45/0.85，会把新默认 0.35 抬回去）</summary>
    private static void Validate()
    {
        PreloadThreshold = Math.Clamp(PreloadThreshold,
            Constants.PRELOAD_MIN_THRESHOLD, Constants.PRELOAD_MAX_THRESHOLD);
        Set(ConfigSchema.GameCoreCount, ((int)Math.Clamp(GetDouble(ConfigSchema.GameCoreCount, 0), 0, 128)).ToString());
        Set(ConfigSchema.AutoJobHogCpu, ((int)Math.Clamp(GetDouble(ConfigSchema.AutoJobHogCpu, 60), 20, 90)).ToString());
        Set(ConfigSchema.AutoJobHogCpuGame, ((int)Math.Clamp(GetDouble(ConfigSchema.AutoJobHogCpuGame, 40), 20, 90)).ToString());
        Set(ConfigSchema.AutoJobMemTrigger, ((int)Math.Clamp(GetDouble(ConfigSchema.AutoJobMemTrigger, 40), 10, 80)).ToString());
        Set(ConfigSchema.AutoJobMaxTargets, ((int)Math.Clamp(GetDouble(ConfigSchema.AutoJobMaxTargets, 3), 1, 10)).ToString());
        Set(ConfigSchema.AutoJobMaxTargetsGame, ((int)Math.Clamp(GetDouble(ConfigSchema.AutoJobMaxTargetsGame, 6), 1, 12)).ToString());
        Set(ConfigSchema.MemoryReclaimTrigger, ((int)Math.Clamp(GetDouble(ConfigSchema.MemoryReclaimTrigger, 60), 30, 90)).ToString());
    }
}
}
