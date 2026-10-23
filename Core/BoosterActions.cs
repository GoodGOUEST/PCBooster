// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Core/BoosterActions.cs
// 全局动作（游戏页按钮与托盘菜单共用）
// 说明: “一键还原”必须一次把四类副作用都收回去 ——
// 优先级/亲和性、Job 限速、后台核心罩、电源与频率。
// 原实现只还原了前两类之外的一部分，托盘里也没有入口。
// ============================================================
using System;
using PCBooster.Execution.Jobs;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;

namespace PCBooster.Core
{
    /// <summary>跨页面/托盘共用的全局动作</summary>
    public static class BoosterActions
    {
        /// <summary>还原全部优化，返回（进程数, Job 数）</summary>
        public static (int processes, int jobs) RestoreAll()
        {
            int processes = 0, jobs = 0;
            try { processes = ProcessSafeHelper.RestoreAll(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { jobs = JobObjectApi.GetJobCount(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { JobObjectApi.ReleaseAll(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { AutoJobManager.ReleaseAllLimits(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { InstructionSetOptimizer.ResetState(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { WorkingSetLock.ReleaseAllFloors(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { FrequencyManager.RestoreAutoFrequency(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            try { NativeApi.RestorePowerSettings(); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.Debug("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
            AppConfig.ForceSave();
            AppLogger.Info($"♻ 一键还原: {processes} 个进程, {jobs} 个 Job");
            return (processes, jobs);
        }
    }
}
