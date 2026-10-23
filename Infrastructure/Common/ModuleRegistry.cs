// ============================================================
// PC Booster v3.6.8
// Infrastructure/Common/ModuleRegistry.cs
// 修复: D8 Names 尾随空格（健康键一致性）
// ============================================================
namespace PCBooster.Infrastructure.Common
{
/// <summary>模块与决策注册表</summary>
public static class ModuleRegistry
{
public static class Names
{
public const string SchedulerCoordinator = "SchedulerCoordinator";
public const string IntelligenceEngine = "IntelligenceEngine";
public const string PowerSaver = "PowerSaver";
public const string UnifiedPreloader = "UnifiedPreloader";
public const string PreloadProtection = "PreloadProtection";
public const string MemoryReclaimer = "MemoryReclaimer";
public const string ProcessRuleEngine = "ProcessRuleEngine";
public const string AutoJobManager = "AutoJobManager";
public const string NativeApi = "NativeApi";
}
    public static class Decisions
    {
        public const string Power = "Power";
        public const string Memory = "Memory";
        public const string Rule = "Rule";
        public const string Preload = "Preload";
        public const string Priority = "Priority";
        public const string Auxiliary = "Auxiliary";
        public const string InstructionSet = "InstructionSet";
    }
    public static readonly List<string> AllModules = new()
    {
        Names.SchedulerCoordinator, Names.IntelligenceEngine, Names.PowerSaver,
        Names.UnifiedPreloader, Names.PreloadProtection, Names.MemoryReclaimer,
        Names.ProcessRuleEngine, Names.AutoJobManager, Names.NativeApi
    };
    private static readonly HashSet<string> _critical = new()
    {
        Names.SchedulerCoordinator, Names.IntelligenceEngine,
        Names.PowerSaver, Names.PreloadProtection, Names.NativeApi
    };
    public static int TotalMonitoredModules => AllModules.Count;
    public static bool IsCritical(string m) => _critical.Contains(m);
    public static string GetModuleForDecision(string decision) => decision switch
    {
        Decisions.Power => Names.PowerSaver,
        Decisions.Memory => Names.MemoryReclaimer,
        Decisions.Rule => Names.ProcessRuleEngine,
        Decisions.Preload => Names.UnifiedPreloader,
        Decisions.Priority => Names.NativeApi,
        Decisions.InstructionSet => Names.NativeApi,
        Decisions.Auxiliary => Names.AutoJobManager,   // ★ 3.6.8: AutoJob 真正接入健康/休眠门控
        _ => Names.SchedulerCoordinator
    };
}
}