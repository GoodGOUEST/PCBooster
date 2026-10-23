// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Execution/Rules/ProcessRuleEngine.cs
// 规则引擎（合并 RuleApplier + 亲和性走合并器）
// ============================================================

using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;
using PCBooster.Intelligence;

namespace PCBooster.Execution.Rules
{
    /// <summary>规则引擎</summary>
    public static class ProcessRuleEngine
    {
        private static readonly List<ProcessRule> _rules = new();
        private static readonly object _lock = new();
        private static int _version;
        // 已应用过的 (PID|规则) 去重，支持“新进程出现时再应用规则”而不重复刷统计
        private static readonly HashSet<string> _appliedKeys = new();
        private static PersistenceManager<List<ProcessRule>> _persistence;

        public static void Initialize()
        {
            _persistence = new PersistenceManager<List<ProcessRule>>(Constants.FILE_RULES);
            var loaded = _persistence.Load();
            if (loaded != null && loaded.Count > 0)
            {
                lock (_lock) _rules.AddRange(loaded);
                RebuildIndex();
                // 历史规则迁移——旧版本"新建"产生的 "*" 空动作规则会把所有进程优先级重置为 Normal
                int disabled = 0;
                lock (_lock)
                    foreach (var r in _rules)
                    {
                        if (!r.IsEnabled) continue;
                        if (r.ProcessPattern == "*" && (r.Action ?? "").Trim().Length == 0 &&
                            r.TargetAffinityMask == -1)
                        { r.IsEnabled = false; disabled++; }
                    }
                if (disabled > 0)
                {
                    AppLogger.Warning($"⚠️ 已禁用 {disabled} 条历史 '*' 无动作规则（旧版本会导致全局优先级重置）");
                    Save();
                }
                AppLogger.Info($"📋 规则已加载: {_rules.Count} 条");
            }
        }

        public static int GetVersion() => _version;
        /// <summary>是否存在启用中的规则（供调度器决定是否每轮检查新进程）</summary>
        public static bool HasActiveRules { get { lock (_lock) return _rules.Any(r => r.IsEnabled); } }
        public static void RebuildIndex() { lock (_lock) { _version++; _appliedKeys.Clear(); } }

        public static List<ProcessRule> GetAllRules()
        { lock (_lock) return new List<ProcessRule>(_rules); }

        public static List<ProcessRule> GetActiveRules()
        { lock (_lock) return _rules.Where(r => r.IsEnabled).ToList(); }

        public static void AddRule(ProcessRule rule)
        {
            if (rule == null) return;
            lock (_lock) { _rules.Add(rule); _version++; _appliedKeys.Clear(); _persistence.MarkDirty(); }
        }

        public static void RemoveRule(string ruleId)
        {
            lock (_lock)
            {
                _rules.RemoveAll(r => r.Id == ruleId);
                _version++;
                _appliedKeys.Clear();
                _persistence.MarkDirty();
            }
        }

        public static int ImportRules(List<ProcessRule> rules, string source)
        {
            if (rules == null || rules.Count == 0) return 0;
            int imported = 0;
            lock (_lock)
            {
                foreach (var rule in rules)
                {
                    if (_rules.Any(r => r.Name == rule.Name && r.ProcessPattern == rule.ProcessPattern)) continue;
                    rule.Source = source;
                    _rules.Add(rule);
                    imported++;
                }
                if (imported > 0) { _version++; _appliedKeys.Clear(); _persistence.MarkDirty(); }
            }
            return imported;
        }

        public static bool Save()
        {
            lock (_lock) _persistence.ReplaceData(new List<ProcessRule>(_rules));
            return _persistence.Save();
        }

        // ═══════════ 应用器（合并，修复 N3 亲和性走合并器）═══════════

        public static int ApplyAllRules(NativeApi.ProcessSnapshot[] procs, double cpuUsage, double memoryAvailable)
        {
            if (procs == null || procs.Length == 0) return 0;
            var rules = GetActiveRules();
            if (rules.Count == 0) return 0;

            int applied = 0;
            // 去重表按 PID 清理已退出项（防 PID 复用后新进程被误判“已应用”）
            if (_appliedKeys.Count > 256)
            {
                var livePids = new HashSet<int>(procs.Where(p => p.Id > 0).Select(p => p.Id));
                lock (_lock)
                {
                    var stale = _appliedKeys
                        .Where(k => !int.TryParse(k.Split('|')[0], out var pid) || !livePids.Contains(pid))
                        .ToList();
                    foreach (var k in stale) _appliedKeys.Remove(k);
                }
            }
            foreach (var p in procs)
            {
                if (string.IsNullOrEmpty(p.ProcessName)) continue;
                if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
                if (ProcessMatcher.IsProtected(p.ProcessName)) continue;   // 红线: 规则也不碰反作弊

                var rule = rules
                    .Where(r => !r.IsNoOp)
                    .Where(r => r.Matches(p.ProcessName))
                    .OrderBy(r => r.Priority)
                    .FirstOrDefault(r => r.CheckCondition(cpuUsage, memoryAvailable));

                if (rule == null) continue;
                // 同一 (PID, 规则) 只应用一次；新启动的进程会在下一轮被补上
                var key = p.Id + "|" + rule.Id;
                lock (_lock) if (_appliedKeys.Contains(key)) continue;
                if (ApplyRuleToProcess(p, rule))
                {
                    applied++;
                    lock (_lock)
                    {
                        if (_appliedKeys.Count > 4096) _appliedKeys.Clear();
                        _appliedKeys.Add(key);
                    }
                }
            }

            if (applied > 0) AppLogger.Debug($"📋 规则应用: {applied} 个进程");
            return applied;
        }

        private static bool ApplyRuleToProcess(NativeApi.ProcessSnapshot p, ProcessRule rule)
        {
            bool applied = false;
            string action = rule.NormalizedAction();

            if (action == "priority")
            {
                var priority = rule.TargetPriority switch
                {
                    0 => System.Diagnostics.ProcessPriorityClass.Idle,
                    1 => System.Diagnostics.ProcessPriorityClass.BelowNormal,
                    2 => System.Diagnostics.ProcessPriorityClass.Normal,
                    3 => System.Diagnostics.ProcessPriorityClass.AboveNormal,
                    _ => System.Diagnostics.ProcessPriorityClass.High
                };
                applied |= ProcessSafeHelper.SetPriority(p.Id, priority);
            }

            // N3修复: 亲和性走 AffinityResolver（用户规则最高优先级）
            // K3修复: long → ulong 显式转换（匹配新签名）
            if (rule.TargetAffinityMask != -1)
            {
                applied |= AffinityResolver.ApplyAffinity(
                    p.Id, p.ProcessName,
                    userRuleMask: (ulong)rule.TargetAffinityMask);
            }

            // Action="preload" 真正可执行（原字段被完全忽略，学习规则因此名存实亡）
            if (action == "preload")
            {
                _ = PCBooster.Execution.Preload.UnifiedPreloader.PreloadForProcess(p.Id, p.ProcessName, 1.0);
                applied = true;
            }

            if (applied)
            {
                rule.HitCount++;
                rule.LastApplied = DateTime.Now;
                IntelligenceEngine.OnRuleHit(p.ProcessName);
                EventBus.Publish(new RuleAppliedEvent(p.ProcessName, rule.Name, DateTime.Now, EventBus.NextSeq()));
            }
            return applied;
        }

        public static string GetStats()
        {
            lock (_lock)
            {
                int active = _rules.Count(r => r.IsEnabled);
                return $"📋 规则引擎: {_rules.Count} 条 ({active} 活跃)";
            }
        }
    }
}
