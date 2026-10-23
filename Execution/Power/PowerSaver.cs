// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Execution/Power/PowerSaver.cs
// ============================================================
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;
namespace PCBooster.Execution.Power
{
/// <summary>电源管理器（3级状态机 + 模块休眠）</summary>
public static class PowerSaver
{
public enum PowerState { Active, LightStandby, DeepSleep }
public enum WakeReason { UserActivity, Prediction, HighLoad, ScheduledTask }
    private class ModuleState
    {
        public string Name;
        public int Priority;
        public bool IsSleeping;
        public DateTime LastAwake;
    }
    private static readonly object _lock = new();
    private static PowerState _currentState = PowerState.Active;
    private static DateTime _lastUserActivity = DateTime.Now;
    private static readonly Dictionary<string, ModuleState> _modules = new();
    private static SafeTimer _idlePollTimer;
    public static event Action<PowerState> OnStateChanged;
    public static void Initialize()
    {
        RegisterModule(ModuleRegistry.Names.PreloadProtection, 1);
        RegisterModule(ModuleRegistry.Names.PowerSaver, 2);
        RegisterModule(ModuleRegistry.Names.SchedulerCoordinator, 3);
        RegisterModule(ModuleRegistry.Names.NativeApi, 4);
        RegisterModule(ModuleRegistry.Names.ProcessRuleEngine, 5);
        RegisterModule(ModuleRegistry.Names.UnifiedPreloader, 6);
        RegisterModule(ModuleRegistry.Names.IntelligenceEngine, 1);
        // 这两个模块此前从未注册 → IsModuleSleeping 永远 false，
        // 深度休眠/轻待命的模块门控对它们完全无效。
        RegisterModule(ModuleRegistry.Names.MemoryReclaimer, 3);
        RegisterModule(ModuleRegistry.Names.AutoJobManager, 4);
        _idlePollTimer = new SafeTimer("UserActivityPoll", PollGlobalInput,
            TimeSpan.FromSeconds(Constants.LAST_INPUT_POLL_SECONDS),
            TimeSpan.FromSeconds(Constants.LAST_INPUT_POLL_SECONDS),
            ModuleRegistry.Names.PowerSaver);
    }
    public static void RegisterModule(string name, int priority)
    {
        lock (_lock)
            _modules[name] = new ModuleState
            { Name = name, Priority = priority, IsSleeping = false, LastAwake = DateTime.Now };
    }
    /// <summary>模块休眠查询（供调度器门控真正生效）</summary>
    public static bool IsModuleSleeping(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName)) return false;
        lock (_lock) return _modules.TryGetValue(moduleName, out var m) && m.IsSleeping;
    }
    private static void PollGlobalInput()
    {
        if (!NativeApi.IsSessionActive()) return;
        if (NativeApi.GetGlobalIdleSeconds() < 2) OnUserActivity();
    }
    // 电源来源变化（笔记本拔插电）时立即重算，而不是等下次进游戏
    private static bool? _lastOnAc;

    private static void CheckPowerSource()
    {
        try
        {
            bool onAc = NativeApi.IsOnAcPower();
            if (_lastOnAc == null) { _lastOnAc = onAc; return; }
            if (_lastOnAc.Value == onAc) return;
            _lastOnAc = onAc;
            AppLogger.Info(onAc ? "🔌 已接通电源" : "🔋 已切换到电池");
            if (!onAc)
            {
                // 电池上不保留激进电源档（省电 / 降温）
                Infrastructure.Profiling.FrequencyManager.RestoreAutoFrequency();
            }
            else if (Intelligence.IntelligenceEngine.GetScene() == Constants.SCENE_GAMING)
            {
                Infrastructure.Profiling.FrequencyManager.LockMaxFrequency("接通电源", 0);
            }
        }
        catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
    }

    public static void CheckWakeConditions()
    {
        CheckPowerSource();
        PowerState currentState; DateTime lastActivity;
        lock (_lock) { currentState = _currentState; lastActivity = _lastUserActivity; }
        var idle = DateTime.Now - lastActivity;
        if (idle.TotalSeconds < 2)
        {
            if (currentState != PowerState.Active) SetState(PowerState.Active);
            return;
        }
        // 系统有形负载也算“在忙”——挂机编译/渲染/下载时不再降核
        //（原实现只看键鼠空闲，完全可能把正在干活的机器压到 20% 核心）
        if (NativeApi.GetCpuUsage() > Constants.DEEP_SLEEP_CPU_GUARD)
        {
            lock (_lock) _lastUserActivity = DateTime.Now;
            if (currentState != PowerState.Active) SetState(PowerState.Active);
            return;
        }
        if (currentState == PowerState.Active &&
            idle.TotalSeconds > Constants.LIGHT_STANDBY_TIMEOUT_SECONDS)
            SetState(PowerState.LightStandby);
        else if (currentState == PowerState.LightStandby &&
                 idle.TotalSeconds > Constants.DEEP_SLEEP_TIMEOUT_SECONDS)
            SetState(PowerState.DeepSleep);
    }
    public static void SetState(PowerState newState)
    {
        PowerState oldState; bool changed = false;
        lock (_lock)
        {
            if (_currentState == newState) return;
            oldState = _currentState;
            _currentState = newState;
            changed = true;
            switch (newState)
            {
                case PowerState.Active:
                    NativeApi.RestoreParkingBaseline();   // 回到用户原值，而不是固定 100%
                    WakeAllLocked();
                    break;
                case PowerState.LightStandby:
                    if (!FrequencyManager.IsLocked)
                        NativeApi.SetCoreParkingPercent(Constants.CORE_PARKING_LIGHT_STANDBY);
                    SleepLowPriorityLocked();
                    break;
                case PowerState.DeepSleep:
                    if (!FrequencyManager.IsLocked)
                        NativeApi.SetCoreParkingPercent(Constants.CORE_PARKING_DEEP_SLEEP);
                    SleepAllLocked();
                    break;
            }
        }
        if (changed)
        {
            EventBus.Publish(new PowerStateChangedEvent(oldState.ToString(), newState.ToString(),
                DateTime.Now, EventBus.NextSeq()));
            OnStateChanged?.Invoke(newState);
        }
    }
    public static void WakeUp(PowerState target, WakeReason reason)
    {
        PowerState old; bool changed = false;
        lock (_lock)
        {
            old = _currentState;
            if (_currentState != target)
            {
                _currentState = target;
                changed = true;
                if (target == PowerState.Active)
                {
                    NativeApi.RestoreParkingBaseline();   // 恢复原停泊值，不再固定 100%
                    WakeAllLocked();
                }
            }
        }
        if (changed)
            EventBus.Publish(new PowerStateChangedEvent(old.ToString(), target.ToString(),
                DateTime.Now, EventBus.NextSeq()));
        AppLogger.Info($"⏰ 唤醒: {reason} → {target}");
    }
    public static void OnUserActivity()
    {
        lock (_lock) _lastUserActivity = DateTime.Now;
        // 复用 WakeUp（原实现手写一遍休眠状态回切，且 WakeUp 成为死代码）
        if (GetCurrentState() != PowerState.Active)
            WakeUp(PowerState.Active, WakeReason.UserActivity);
    }
    public static PowerState GetCurrentState()
    { lock (_lock) return _currentState; }
    public static void WakeModule(string moduleName)
    {
        lock (_lock)
        {
            if (_modules.TryGetValue(moduleName, out var m))
            {
                if (!m.IsSleeping)
                {
                    if ((DateTime.Now - m.LastAwake).TotalSeconds < 2) return;
                    m.LastAwake = DateTime.Now;
                    return;
                }
                m.IsSleeping = false;
                m.LastAwake = DateTime.Now;
            }
        }
    }
    private static void WakeAllLocked()
    {
        foreach (var m in _modules.Values)
        { m.IsSleeping = false; m.LastAwake = DateTime.Now; }
    }
    /// <summary>轻待命休眠优先级≥5模块（原>=7 永不触发）</summary>
    private static void SleepLowPriorityLocked()
    {
        foreach (var m in _modules.Values)
            if (m.Priority >= Constants.MODULE_SLEEP_PRIORITY_LIGHT && !m.IsSleeping && !ModuleRegistry.IsCritical(m.Name))
                m.IsSleeping = true;
    }
    private static void SleepAllLocked()
    {
        foreach (var m in _modules.Values)
            if (!m.IsSleeping && !ModuleRegistry.IsCritical(m.Name))
                m.IsSleeping = true;
    }
    public static string GetStateName(PowerState state) => state switch
    {
        PowerState.Active => "活跃",
        PowerState.LightStandby => "轻度待命",
        PowerState.DeepSleep => "深度休眠",
        _ => "⚪ 未知"
    };
    public static string GetStats()
    {
        lock (_lock)
        {
            int sleeping = 0, awake = 0;
            foreach (var m in _modules.Values) { if (m.IsSleeping) sleeping++; else awake++; }
            return $"🔋 电源: {GetStateName(_currentState)} | 模块 {sleeping} 休眠 / {awake} 活跃";
        }
    }
}
}
