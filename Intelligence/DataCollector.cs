// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// Intelligence/DataCollector.cs
// 96槽/序列/净化/活动增量具名化
// 3.6.8 走查:
// ① 场景识别与学习采集解耦（learn=false 时仍更新场景，但不落任何统计）；
// ② 场景以“前台进程”为准（游戏在场优先），不再被后台常驻 IDE 劫持；
// ③ 所有键统一 Sanitize（大写进程名也能查到档案/写反馈）；
// ④ 删除只写不读的 HourlyActivity / WeeklyLaunches / ForegroundSwitches。
// ============================================================
using System.Text;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;

namespace PCBooster.Intelligence
{
    /// <summary>统一数据采集器（每周期 1 次遍历）</summary>
    public class DataCollector
    {
        private readonly Dictionary<string, ProcessData> _processes = new();
        private readonly object _lock = new();
        private readonly List<string> _recentSequence = new();
        private string _currentScene = Constants.SCENE_GENERAL;
        private string _lastScene = Constants.SCENE_GENERAL;
        // 场景去抖状态（候选场景 + 连续命中次数）
        private string _pendingScene = "";
        private int _pendingStreak;
        private long _collectCount;
        private int _lastForegroundPid;   // 前台切换检测（用户日常最频繁的行为）

        public class ProcessData
        {
            public string Name = "";
            public SlidingWindow<double> CpuWindow = new(Constants.PROFILER_WINDOW_SIZE);
            public SlidingWindow<long> WorkingSetWindow = new(Constants.PROFILER_WINDOW_SIZE);
            public int ForegroundCount, TotalSamples, TotalLaunches;
            public double TotalRunMinutes;
            public int[] HourlyLaunches = new int[Constants.TIME_SLOTS_PER_DAY];
            // “使用”维度（启动 + 前台切换），预测主信号
            public int[] HourlyUsage = new int[Constants.TIME_SLOTS_PER_DAY];
            public int[] WeeklyUsage = new int[7];
            public DateTime LastForegroundAt = DateTime.MinValue;
            public string UserOverride = "";
            public DateTime FirstSeen = DateTime.Now, LastSeen = DateTime.Now;
        }

        /// <param name="learn">是否记录行为统计（受“数据采集”开关控制）。
        /// 场景/前台跟踪必须始终运行——否则默认关闭采集时，
        /// 游戏模式、电源锁定、核心隔离全部不会触发（实际“没作用”的根因）。</param>
        public void Collect(NativeApi.ProcessSnapshot[] procs, List<string> justLaunched, int fgPid,
            bool learn = true)
        {
            if (procs == null) return;
            string toPublish = null, oldScene = null;
            lock (_lock)
            {
                if (learn) _collectCount++;
                bool sampleCpu = learn && _collectCount % Constants.PROFILER_SAMPLE_EVERY_N_CYCLES == 0;
                var now = DateTime.Now;
                int perSlotMinutes = 1440 / Constants.TIME_SLOTS_PER_DAY;
                int slot = now.Hour * (Constants.TIME_SLOTS_PER_DAY / 24) + now.Minute / perSlotMinutes;

                // 场景判定只看“本次快照里活着的进程”，不再使用历史档案字典
                var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in procs)
                {
                    if (string.IsNullOrEmpty(p.ProcessName)) continue;
                    if (ProcessMatcher.IsSystemProcess(p.ProcessName)) continue;
                    var name = Sanitize(p.ProcessName);
                    liveNames.Add(name);
                    if (!learn) continue;   // 未开采集：只做场景判定，不落任何统计

                    if (!_processes.TryGetValue(name, out var data))
                    {
                        if (_processes.Count >= Constants.MAX_PROCESSES_TRACKED) continue;
                        data = new ProcessData { Name = name, FirstSeen = now };
                        _processes[name] = data;
                    }
                    data.WorkingSetWindow.Add(p.WorkingSet64);
                    if (sampleCpu) data.CpuWindow.Add(ProcessHelper.GetProcessCpuUsage(p.Id));
                    if (p.Id == fgPid) { data.ForegroundCount++; data.LastForegroundAt = now; }
                    data.TotalSamples++;
                    data.LastSeen = now;
                    data.TotalRunMinutes += Constants.RUN_MINUTES_PER_TICK; // 
                }

                if (learn && justLaunched != null)
                {
                    foreach (var raw in justLaunched)
                    {
                        if (string.IsNullOrEmpty(raw)) continue;
                        var name = Sanitize(raw);
                        if (ProcessMatcher.IsSystemProcess(name)) continue;
                        if (_processes.TryGetValue(name, out var d))
                        {
                            d.TotalLaunches++;
                            d.HourlyLaunches[slot]++;
                            d.HourlyUsage[slot]++;                    // 使用维度
                            d.WeeklyUsage[(int)now.DayOfWeek]++;
                        }
                        _recentSequence.Add(name);
                        if (_recentSequence.Count > Constants.SEQUENCE_MAX_LENGTH) _recentSequence.RemoveAt(0);
                    }
                }

                // 前台名只算一次，供“学习记账”与“场景判定”共用
                string fgName = null;
                if (fgPid > 0)
                    foreach (var sp in procs)
                        if (sp.Id == fgPid) { fgName = Sanitize(sp.ProcessName); break; }

                // 记录“前台切换”——原来只学“启动”，而用户日常主要是切窗口
                if (learn && fgPid > 0 && fgPid != _lastForegroundPid)
                {
                    _lastForegroundPid = fgPid;
                    if (!string.IsNullOrEmpty(fgName) && !ProcessMatcher.IsSystemProcess(fgName))
                    {
                        if (_processes.TryGetValue(fgName, out var fd))
                        {
                            fd.HourlyUsage[slot]++;
                            fd.WeeklyUsage[(int)now.DayOfWeek]++;
                            fd.LastForegroundAt = now;
                        }
                        _recentSequence.Add(fgName);
                        if (_recentSequence.Count > Constants.SEQUENCE_MAX_LENGTH) _recentSequence.RemoveAt(0);
                    }
                }

                string rawScene = DetectScene(liveNames, fgName);
                // 场景判定去抖（见 ConfirmScene）——抖动不再产生切换事件
                string confirmedScene = ConfirmScene(rawScene, out string previousScene);
                if (confirmedScene != null)
                {
                    oldScene = previousScene;
                    toPublish = confirmedScene;
                }
            }
            if (toPublish != null)
                EventBus.Publish(new SceneChangedEvent(oldScene, toPublish, DateTime.Now, EventBus.NextSeq()));
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (!char.IsControl(c) && !char.IsSurrogate(c)) sb.Append(c);
            var r = sb.ToString().Trim().ToLowerInvariant();
            return r.Length > Constants.SANITIZE_MAX_LENGTH ? r[..Constants.SANITIZE_MAX_LENGTH] : r;
        }

        /// <summary>降采样（保序，保留末位），用于压缩持久化的滑窗样本</summary>
        private static T[] Downsample<T>(List<T> src, int step)
        {
            if (src.Count == 0) return Array.Empty<T>();
            if (step <= 1 || src.Count <= 4) return src.ToArray();
            var dst = new List<T>(src.Count / step + 2);
            for (int i = 0; i < src.Count; i += step) dst.Add(src[i]);
            if ((src.Count - 1) % step != 0) dst.Add(src[src.Count - 1]);
            return dst.ToArray();
        }

        /// <summary>场景去抖：全屏游戏切显示模式/加载/退出确认框时，前台进程会秒级闪烁，
        /// 原始判定在 gaming ↔ gaming-bg 之间来回翻（实测 10 秒翻了 5 次），每一次都会：
        /// 重置电源方案 + 扫核隔离 + 刷反馈噪声 + 刷 UI 日志。
        /// 所以非游戏场景转换要连续 SCENE_CONFIRM_SAMPLES 次一致才发布；
        /// 回到当前已确认场景时直接把候选清掉（交替闪烁因此永远不会产生事件）。
        /// 进入游戏仍立即生效（用户要马上有提升；电源侧还有 3s 稳定闸门兼顾）。</summary>
        private string ConfirmScene(string raw, out string previous)
        {
            previous = _currentScene;
            if (string.IsNullOrEmpty(raw)) return null;
            // ① 直接回到已确认场景 → 清候选，不发布
            if (raw == _currentScene) { _pendingScene = ""; _pendingStreak = 0; return null; }
            // ② 累计候选
            if (raw == _pendingScene) _pendingStreak++;
            else { _pendingScene = raw; _pendingStreak = 1; }
            bool immediate = raw == Constants.SCENE_GAMING;
            if (!immediate && _pendingStreak < Constants.SCENE_CONFIRM_SAMPLES) return null;
            // ③ 确认切换
            _pendingScene = ""; _pendingStreak = 0;
            _lastScene = raw;
            _currentScene = raw;
            return raw;
        }

        /// <summary>会议类复用匹配器（删除内联重复）
        /// 除游戏外，场景以前台进程为准；否则后台常驻的 IDE/Office
        /// 会把场景永久劫持（用户明明在浏览网页，场景却一直是 development）</summary>
        private string DetectScene(HashSet<string> names, string foregroundName)
        {
            names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // ① 游戏在运行：只要它在前台就保持 gaming；切到桌面/浏览器时降为 gaming-bg，
            // 旧版一旦游戏启动就永远 gaming，最小化后依然锁频+高性能电源，
            // 用户回桌面办公/看视频时机器持续发热、风扇响。
            if (names.Any(ProcessMatcher.IsGame))
            {
                bool foregroundIsGame = !string.IsNullOrEmpty(foregroundName) && ProcessMatcher.IsGame(foregroundName);
                return foregroundIsGame ? Constants.SCENE_GAMING : Constants.SCENE_GAMING_BACKGROUND;
            }
            // 影音播放（前台是播放器）—— 只关核心停泊，不切高性能、不锁频
            if (!string.IsNullOrEmpty(foregroundName) && ProcessMatcher.IsVideoPlayer(foregroundName))
                return Constants.SCENE_VIDEO;
            // ② 其余场景以前台为准
            if (!string.IsNullOrEmpty(foregroundName))
            {
                if (ProcessMatcher.IsDesignTool(foregroundName) || ProcessMatcher.IsVideoEditor(foregroundName))
                    return Constants.SCENE_DESIGN;
                if (ProcessMatcher.IsDevTool(foregroundName)) return Constants.SCENE_DEVELOPMENT;
                if (ProcessMatcher.IsMeetingTool(foregroundName)) return Constants.SCENE_MEETING;
                if (ProcessMatcher.IsOffice(foregroundName)) return Constants.SCENE_OFFICE;
                if (ProcessMatcher.IsBrowser(foregroundName)) return Constants.SCENE_BROWSING;
            }
            // ③ 前台是桌面/系统进程时，用“在场程序”兜底
            if (names.Any(n => ProcessMatcher.IsDesignTool(n) || ProcessMatcher.IsVideoEditor(n)))
                return Constants.SCENE_DESIGN;
            if (names.Any(n => ProcessMatcher.IsDevTool(n))) return Constants.SCENE_DEVELOPMENT;
            if (names.Any(n => ProcessMatcher.IsMeetingTool(n))) return Constants.SCENE_MEETING;
            if (names.Any(n => ProcessMatcher.IsOffice(n))) return Constants.SCENE_OFFICE;
            if (names.Any(n => ProcessMatcher.IsBrowser(n))) return Constants.SCENE_BROWSING;
            return Constants.SCENE_GENERAL;
        }

        public void OnFeedback(string processName, bool hit)
        {
            var key = Sanitize(processName);   // 统一键（大写进程名也能命中）
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_processes.TryGetValue(key, out var data))
                {
                    int slot = DateTime.Now.Hour * (Constants.TIME_SLOTS_PER_DAY / 24) +
                               DateTime.Now.Minute / (1440 / Constants.TIME_SLOTS_PER_DAY);
                    if (hit) data.HourlyLaunches[slot]++;
                    else data.HourlyLaunches[slot] = Math.Max(0, data.HourlyLaunches[slot] - 1);
                }
            }
        }

        public void OnRuleHit(string processName)
        {
            var key = Sanitize(processName);   // 统一键
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
                if (_processes.TryGetValue(key, out var data)) data.TotalLaunches++;
        }

        public void SetUserOverride(string processName, string classification)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            // 统一规范化键（UI 传入的是原始大小写名称）
            var key = Sanitize(processName);
            var cls = classification ?? "";
            lock (_lock)
            {
                // 进程尚未进入档案时也能生效（采集关闭时 UI 标记不再是空操作）
                if (!_processes.TryGetValue(key, out var data))
                {
                    data = new ProcessData { Name = key };
                    _processes[key] = data;
                }
                data.UserOverride = cls;
            }
        }

        public ProcessData GetProcess(string name)
        {
            // 统一键——原来是大小写敏感查字典，
            // WeChat / Code 这类大写进程名查不到档案，用户覆盖与统计全部失效。
            var key = Sanitize(name);
            if (key.Length == 0) return null;
            lock (_lock) return _processes.TryGetValue(key, out var d) ? d : null;
        }

        public Dictionary<string, ProcessData> GetAllProcesses()
        { lock (_lock) return new Dictionary<string, ProcessData>(_processes); }

        public List<string> GetRecentSequence()
        { lock (_lock) return new List<string>(_recentSequence); }

        public string GetCurrentScene() => _currentScene;

        public int GetProcessCount()
        { lock (_lock) return _processes.Count; }

        /// <summary>有效采集时长（分钟）——用于冷启动判定与 UI 进度</summary>
        public int CollectedMinutes
        { get { lock (_lock) return (int)(_collectCount * Constants.FAST_HEARTBEAT_MS / 60000.0); } }

        public void CleanStale()
        {
            lock (_lock)
            {
                var cutoff = DateTime.Now.AddDays(-Constants.PROFILER_STALE_DAYS);
                var stale = _processes.Where(kv => kv.Value.LastSeen < cutoff)
                                      .Select(kv => kv.Key).ToList();
                foreach (var name in stale) _processes.Remove(name);
                if (stale.Count > 0) AppLogger.Debug($"📊 清理过期档案: {stale.Count} 个");
            }
        }

        public void Export(LearningData data)
        {
            lock (_lock)
            {
                foreach (var kv in _processes)
                {
                    data.Processes[kv.Key] = new ProcessDataDto
                    {
                        Name = kv.Value.Name,
                        // 降采样持久化（每 6 个取 1 → 60 点变 10 点）
                        CpuSamples = Downsample(kv.Value.CpuWindow.ToList(), 6),
                        WorkingSetSamples = Downsample(kv.Value.WorkingSetWindow.ToList(), 6),
                        ForegroundCount = kv.Value.ForegroundCount,
                        TotalSamples = kv.Value.TotalSamples,
                        TotalLaunches = kv.Value.TotalLaunches,
                        TotalRunMinutes = kv.Value.TotalRunMinutes,
                        HourlyLaunches = (int[])kv.Value.HourlyLaunches.Clone(),
                        HourlyUsage = (int[])kv.Value.HourlyUsage.Clone(),
                        WeeklyUsage = (int[])kv.Value.WeeklyUsage.Clone(),
                        LastForegroundAt = kv.Value.LastForegroundAt,
                        UserOverride = kv.Value.UserOverride,
                        FirstSeen = kv.Value.FirstSeen,
                        LastSeen = kv.Value.LastSeen
                    };
                }
                data.CurrentScene = _currentScene;
            }
        }

        public void Load(LearningData data)
        {
            if (data?.Processes == null) return;
            lock (_lock)
            {
                foreach (var kv in data.Processes)
                {
                    var dto = kv.Value;
                    if (dto == null) continue;
                    // 旧/手改 JSON 的键可能不是规范小写，载入时统一
                    var key = Sanitize(string.IsNullOrEmpty(dto.Name) ? kv.Key : dto.Name);
                    if (key.Length == 0) continue;
                    var proc = new ProcessData
                    {
                        Name = key,
                        ForegroundCount = dto.ForegroundCount,
                        TotalSamples = dto.TotalSamples,
                        TotalLaunches = dto.TotalLaunches,
                        TotalRunMinutes = dto.TotalRunMinutes,
                        HourlyLaunches = dto.HourlyLaunches ?? new int[Constants.TIME_SLOTS_PER_DAY],
                        HourlyUsage = dto.HourlyUsage ?? new int[Constants.TIME_SLOTS_PER_DAY],
                        WeeklyUsage = dto.WeeklyUsage ?? new int[7],
                        LastForegroundAt = dto.LastForegroundAt,
                        UserOverride = dto.UserOverride ?? "",
                        FirstSeen = dto.FirstSeen,
                        LastSeen = dto.LastSeen
                    };
                    proc.CpuWindow.AddRange(dto.CpuSamples ?? Array.Empty<double>());
                    proc.WorkingSetWindow.AddRange(dto.WorkingSetSamples ?? Array.Empty<long>());
                    _processes[key] = proc;
                }
                _currentScene = data.CurrentScene ?? Constants.SCENE_GENERAL;
                _lastScene = _currentScene;
                // 载入后清空去抖候选，避免旧候选直接生效
                _pendingScene = "";
                _pendingStreak = 0;
            }
        }
    }
}
