// ============================================================
// PC Booster v3.6.8
// UI/AdvancedControlPage.cs
// 设置页（UI 重写）：全部参数集中，两列卡片 + 自动测量行高 + 自动保存
// 与旧版区别: 不再用固定坐标；每行按实测文字高度排布，窗口/DPI 变化不会重叠
// ============================================================
using System;
using System.Drawing;
using System.Windows.Forms;
using PCBooster.Core;
using PCBooster.Execution.Jobs;
using PCBooster.Execution.Memory;
using PCBooster.Execution.Preload;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Profiling;
using PCBooster.Intelligence;

namespace PCBooster.UI
{
    /// <summary>设置页：全部可调项 + 运行诊断</summary>
    public class AdvancedControlPage : BoosterPage
    {
        private bool _loading;
        private System.Windows.Forms.Timer _saveTimer;
        private System.Windows.Forms.Timer _diagTimer;
        private Label _saveState;
        private TextBlock _coreHint, _adminHint, _diag;
        private string _coreResetNote = "";
        private SwitchRow _rowFreqLock, _rowIsolation, _rowWorkingSet, _rowMeta;
        private SwitchRow _rowCollect, _rowLearn, _rowAutoJob, _rowReclaim, _rowTray, _rowStartup;
        private NumberRow _numGameCores, _numThreshold, _numHogCpu, _numHogCpuGame;
        private NumberRow _numMaxTargets, _numMaxTargetsGame, _numMemTrigger, _numReclaimTrigger;
        private TextRow _txtPreloadList;

        private ScrollHost _scroll;

        public AdvancedControlPage() : base(0, "设置", "所有参数集中在这里；改动即时生效并自动保存。")
        {
            BuildLayout();
            ApplyConfigToControls();

            _saveTimer = new System.Windows.Forms.Timer { Interval = 900 };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveNow(); };

            _diagTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _diagTimer.Tick += (s, e) =>
            {
                if (_diag != null) _diag.Text = GetSystemInfo();
                UpdateCoreHint();
            };
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            ApplyConfigToControls();
            _scroll?.ScrollToTop();
            if (_diag != null) _diag.Text = GetSystemInfo();
            _diagTimer?.Start();
        }

        public override void OnPageHidden()
        {
            base.OnPageHidden();
            _diagTimer?.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // ★ 本轮修复: 防抖窗口内退出会丢掉最后几次修改，这里先 flush
                try { if (_saveTimer != null && _saveTimer.Enabled) { _saveTimer.Stop(); SaveNow(); } } catch { }
                _saveTimer?.Stop();
                _saveTimer?.Dispose();
                _diagTimer?.Stop();
                _diagTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ═══════════ 布局 ═══════════
        private void BuildLayout()
        {
            var save = UiToolkit.CreatePrimaryButton("保存设置");
            save.Click += (s, e) => SaveNow();
            Header.AddAction(save);

            _saveState = new Label
            {
                Text = "自动保存已开启",
                AutoSize = true,
                ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall,
                BackColor = Theme.Bg
            };
            Header.AddAction(_saveState);

            var grid = new CardGrid(2);
            BuildGameCard(grid);
            BuildAiCard(grid);
            BuildAutoJobCard(grid);
            BuildMemoryCard(grid);
            BuildAboutCard(grid);
            BuildSafetyCard(grid);
            BuildDiagCard(grid);

            Content.Controls.Add(_scroll = new ScrollHost(grid) { Dock = DockStyle.Fill });
        }

        private void BuildGameCard(CardGrid grid)
        {
            var card = grid.Add(new Card("游戏与性能", "只影响游戏会话；退出游戏自动还原"));

            _rowFreqLock = new SwitchRow("游戏场景锁定最高频率",
                "切到高性能电源方案，并解除核心停泊", AppConfig.GetBool(ConfigSchema.EnableFrequencyLock, true),
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.EnableFrequencyLock, v.ToString());
                    ScheduleSave();
                });
            card.Add(_rowFreqLock);

            _rowIsolation = new SwitchRow("核心隔离调度",
                "游戏用少量高频核心，后台进程移到其余核心", AppConfig.GetBool(ConfigSchema.EnableInstructionSetScheduling, true),
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.EnableInstructionSetScheduling, v.ToString());
                    ScheduleSave();
                });
            card.Add(_rowIsolation);

            _numGameCores = new NumberRow("游戏核心数（物理核）", "0 = 自动；1 个物理核含 2 个线程",
                0, 64, (decimal)AppConfig.GetDouble(ConfigSchema.GameCoreCount, 0), 0, 1,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.GameCoreCount, ((int)v).ToString());
                    UpdateCoreHint();
                    ScheduleSave();
                });
            card.Add(_numGameCores);

            _coreHint = new TextBlock("", Theme.FontSmall, Theme.TextMuted);
            card.Add(_coreHint);

            _rowWorkingSet = new SwitchRow("工作集下限锁定",
                "需管理员权限；与“大页内存”无关", AppConfig.EnableWorkingSetLock,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableWorkingSetLock = v;
                    ScheduleSave();
                    // ★ 本轮修复: 关闭开关时释放已加的工作集下限，否则进程直到退出都保持锁定
                    if (!v) System.Threading.Tasks.Task.Run(() => WorkingSetLock.ReleaseAllFloors());
                });
            card.Add(_rowWorkingSet);

            _rowMeta = new SwitchRow("元数据缓存",
                "预加载去重，减少重复读盘", AppConfig.GetBool(ConfigSchema.EnableMetadataCache, true),
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.EnableMetadataCache, v.ToString());
                    ScheduleSave();
                });
            card.Add(_rowMeta);

            _adminHint = new TextBlock("", Theme.FontSmall, Theme.TextMuted);
            card.Add(_adminHint);

            // ★ 本轮新增: 标准权限时直接给一个提权入口（不再只有一句“需要以管理员身份重启”）
            if (!AppEnvironment.IsElevated)
            {
                var elevate = UiToolkit.CreatePrimaryButton("以管理员重启");
                elevate.Click += (s, e) => AppServices.RequestRestart();
                card.Add(new ButtonRow().Add(elevate).WithNote("UAC 确认后自动续跑，配置不会丢"));
            }
        }

        private void BuildAiCard(CardGrid grid)
        {
            var card = grid.Add(new Card("智能学习", "全部数据仅保存在本机"));

            _rowCollect = new SwitchRow("记录使用习惯",
                "关闭后仍可识别场景（仅内存比对），但预测与置信度停止更新", AppConfig.EnableDataCollection,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableDataCollection = v;
                    ScheduleSave();
                });
            card.Add(_rowCollect);

            _rowLearn = new SwitchRow("闲时学习",
                "电脑空闲时固化规律，不抢占前台资源", AppConfig.EnableIdleLearning,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableIdleLearning = v;
                    ScheduleSave();
                });
            card.Add(_rowLearn);

            _numThreshold = new NumberRow("预加载阈值", "命中率变化时自动升降",
                (decimal)Constants.PRELOAD_MIN_THRESHOLD, (decimal)Constants.PRELOAD_MAX_THRESHOLD,
                (decimal)AppConfig.PreloadThreshold, 2, 0.01m,
                v =>
                {
                    if (_loading) return;
                    AppConfig.PreloadThreshold = (double)v;
                    ScheduleSave();
                });
            card.Add(_numThreshold);

            _txtPreloadList = new TextRow("预加载名单", "进程名，逗号分隔；留空表示自动选择",
                AppConfig.Get(ConfigSchema.PreloadList, ""),
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.PreloadList, v.Trim());
                    ScheduleSave();
                });
            card.Add(_txtPreloadList);

            card.Add(new TextBlock(
                $"已采集 {IntelligenceEngine.GetCollectedMinutes()} 分钟，冷启动需要 {Constants.COLD_START_MINUTES} 分钟。\n" +
                "预加载只会预热“已经在运行”的程序，绝不会自行启动任何程序。",
                Theme.FontSmall, Theme.TextMuted));
        }

        private void BuildAutoJobCard(CardGrid grid)
        {
            var card = grid.Add(new Card("自动限速", "只约束持续吃满单核的后台进程"));

            _rowAutoJob = new SwitchRow("启用自动限速",
                "浏览器 / Office / 开发工具永不限速", AppConfig.EnableAutoJob,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableAutoJob = v;
                    ScheduleSave();
                });
            card.Add(_rowAutoJob);

            _numHogCpu = new NumberRow("触发阈值：单进程 CPU（%）", null,
                20, 90, (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobHogCpu, 60), 0, 5,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.AutoJobHogCpu, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numHogCpu);

            _numHogCpuGame = new NumberRow("游戏中触发阈值（%）", null,
                20, 90, (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobHogCpuGame, 40), 0, 5,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.AutoJobHogCpuGame, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numHogCpuGame);

            _numMemTrigger = new NumberRow("内存触发：可用低于（%）", null,
                10, 80, (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMemTrigger, 40), 0, 5,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.AutoJobMemTrigger, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numMemTrigger);

            _numMaxTargets = new NumberRow("每轮最多限速进程数", null,
                1, 10, (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMaxTargets, 3), 0, 1,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.AutoJobMaxTargets, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numMaxTargets);

            _numMaxTargetsGame = new NumberRow("游戏中最多限速进程数", null,
                1, 12, (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMaxTargetsGame, 6), 0, 1,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.AutoJobMaxTargetsGame, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numMaxTargetsGame);

            card.Add(new TextBlock("1 个核心 = 100%；阈值按“单进程吃满几个核”计算。",
                Theme.FontSmall, Theme.TextMuted));
        }

        private void BuildMemoryCard(CardGrid grid)
        {
            var card = grid.Add(new Card("内存与常规", "内存修剪默认关闭"));

            _rowReclaim = new SwitchRow("启用内存修剪",
                "Windows 自带 standby 列表与内存压缩，强制 Trim 可能增加切回延迟",
                AppConfig.EnableMemoryReclaim,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableMemoryReclaim = v;
                    ScheduleSave();
                });
            card.Add(_rowReclaim);

            _numReclaimTrigger = new NumberRow("回收触发：可用低于（%）", null,
                30, 90, (decimal)AppConfig.GetDouble(ConfigSchema.MemoryReclaimTrigger, 60), 0, 5,
                v =>
                {
                    if (_loading) return;
                    AppConfig.Set(ConfigSchema.MemoryReclaimTrigger, ((int)v).ToString());
                    ScheduleSave();
                });
            card.Add(_numReclaimTrigger);

            _rowTray = new SwitchRow("关闭时驻留系统托盘",
                "点关闭按钮不退出程序，可从托盘图标恢复", AppConfig.EnableTrayResident,
                v =>
                {
                    if (_loading) return;
                    AppConfig.EnableTrayResident = v;
                    ScheduleSave();
                });
            card.Add(_rowTray);

            _rowStartup = new SwitchRow("开机自动启动",
                "优先使用任务计划（管理员），失败自动降级到注册表", AppConfig.LaunchAtStartup,
                v =>
                {
                    if (_loading) return;
                    AppConfig.LaunchAtStartup = v;
                    ScheduleSave();
                    // ★ 本轮修复: 建/删计划任务要走 UAC，原来在 UI 线程同步等，
                    //   点一下开关窗口就“死”住。改到后台线程，并根据实际结果回滚开关。
                    var row = _rowStartup;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        StartupHelper.SetLaunchAtStartup(v);
                        bool ok = StartupHelper.IsLaunchAtStartup();
                        if (ok == v) return;
                        try
                        {
                            if (!IsHandleCreated) return;
                            BeginInvoke((System.Action)(() =>
                            {
                                row.SetChecked(ok);
                                AppConfig.LaunchAtStartup = ok;
                                AppConfig.ForceSave();
                                MessageBox.Show(this,
                                    "开机自启没有设置成功（可能取消了 UAC 确认）。",
                                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }));
                        }
                        catch { }
                    });
                });
            card.Add(_rowStartup);

            var reset = UiToolkit.CreateButton("恢复默认设置");
            reset.Click += (s, e) => ResetDefaults();
            card.Add(new ButtonRow().Add(reset).WithNote("config.ini 可手动编辑"));
        }

        private void BuildDiagCard(CardGrid grid)
        {
            var card = grid.AddWide(new Card("运行诊断", "每 5 秒刷新；数值为引擎实时状态"));
            _diag = new TextBlock(GetSystemInfo(), Theme.FontMono, Theme.TextSecondary);
            card.Add(_diag);
        }

        private void BuildAboutCard(CardGrid grid)
        {
            var card = grid.Add(new Card("关于与数据", "只读信息"));
            card.Add(new TextBlock(
                $"版本 {Constants.VERSION}\n" +
                "配置目录\n" + AppPaths.DataRoot + "\n" +
                "日志目录\n" + AppPaths.LogDir,
                Theme.FontSmall, Theme.TextSecondary));

            var openConfig = UiToolkit.CreateButton("打开配置目录");
            openConfig.Click += (s, e) => OpenPath(AppPaths.DataRoot);
            var openLog = UiToolkit.CreateButton("打开日志目录");
            openLog.Click += (s, e) => OpenPath(AppPaths.LogDir);
            card.Add(new ButtonRow().Add(openConfig).Add(openLog));
        }

        private void BuildSafetyCard(CardGrid grid)
        {
            var card = grid.Add(new Card("安全边界", "设计上不可逾越的红线"));
            card.Add(new TextBlock(
                "不注入进程、不写驱动、不 hook 图形接口。\n" +
                "不修改也不触碰反作弊进程（保护进程会被自动跳过）。\n" +
                "不做网络加速，不替换系统文件。\n\n" +
                "优化只作用于本机进程的优先级、亲和性、工作集与电源方案，\n" +
                "退出程序时会全部还原。",
                Theme.FontSmall, Theme.TextSecondary));
            card.Add(new TextBlock(
                "管理员权限下可用功能最完整；标准权限下会自动降级并提示。",
                Theme.FontSmall, Theme.TextMuted));
        }

        private static void OpenPath(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLogger.Debug($"打开目录失败: {ex.Message}"); }
        }

        private void ResetDefaults()
        {
            if (MessageBox.Show(this, "确定把所有设置恢复为默认值？", "恢复默认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var kv in ConfigSchema.Defaults) AppConfig.Set(kv.Key, kv.Value);
            ApplyConfigToControls();
            SaveNow();
        }

        // ═══════════ 配置读写 ═══════════
        private void ApplyConfigToControls()
        {
            if (_rowFreqLock == null) return;
            _loading = true;
            try
            {
                _rowFreqLock.SetChecked(AppConfig.GetBool(ConfigSchema.EnableFrequencyLock, true));
                _rowIsolation.SetChecked(AppConfig.GetBool(ConfigSchema.EnableInstructionSetScheduling, true));
                _rowWorkingSet.SetChecked(AppConfig.EnableWorkingSetLock);
                _rowMeta.SetChecked(AppConfig.GetBool(ConfigSchema.EnableMetadataCache, true));
                _rowCollect.SetChecked(AppConfig.EnableDataCollection);
                _rowLearn.SetChecked(AppConfig.EnableIdleLearning);
                _rowAutoJob.SetChecked(AppConfig.EnableAutoJob);
                _rowReclaim.SetChecked(AppConfig.EnableMemoryReclaim);
                _rowTray.SetChecked(AppConfig.EnableTrayResident);
                _rowStartup.SetChecked(AppConfig.LaunchAtStartup);

                // ★ 本轮修复: 游戏核数不能占满全部物理核（否则后台罩无核可分，隔离永远不生效）。
                //   拓扑就绪后把上限收紧到“物理核数 - 2”；历史配置里的 16/16 自动退回自动推荐。
                //   关掉“核心隔离调度”时不再限制，允许把全部核给游戏。
                int phys = CpuTopology.PhysicalCoreCount;
                int storedCores = (int)AppConfig.GetDouble(ConfigSchema.GameCoreCount, 0);
                bool isolationOn = AppConfig.GetBool(ConfigSchema.EnableInstructionSetScheduling, true);
                if (phys > 2)
                {
                    int maxGame = isolationOn ? Math.Max(1, phys - 2) : phys;
                    _numGameCores.Maximum = maxGame;
                    int shownCores = storedCores > maxGame ? 0 : storedCores;
                    _numGameCores.Value = shownCores;
                    if (isolationOn && shownCores != storedCores)
                    {
                        // 不做静默落盘（避免测试/查看页面时改写用户配置）；
                        // 引擎那边同样把“占满全核”当作无效值退回自动，两边结果一致。
                        _coreResetNote = $"原设置 {storedCores} 核占满全部物理核，无法隔离，按自动处理";
                    }
                }
                else
                {
                    _numGameCores.Maximum = 64;
                    _numGameCores.Value = storedCores;
                }
                _numThreshold.Value = (decimal)AppConfig.PreloadThreshold;
                _numHogCpu.Value = (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobHogCpu, 60);
                _numHogCpuGame.Value = (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobHogCpuGame, 40);
                _numMemTrigger.Value = (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMemTrigger, 40);
                _numMaxTargets.Value = (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMaxTargets, 3);
                _numMaxTargetsGame.Value = (decimal)AppConfig.GetDouble(ConfigSchema.AutoJobMaxTargetsGame, 6);
                _numReclaimTrigger.Value = (decimal)AppConfig.GetDouble(ConfigSchema.MemoryReclaimTrigger, 60);
                _txtPreloadList.Value = AppConfig.Get(ConfigSchema.PreloadList, "");

                _adminHint.Text = AppEnvironment.IsElevated
                    ? "管理员模式：全部优化可用。"
                    : "标准权限：工作集锁定与限速不可用，点下方按钮提权。";
                _adminHint.ForeColor = AppEnvironment.IsElevated ? Theme.Success : Theme.Warning;
                UpdateCoreHint();
            }
            finally { _loading = false; }
        }

        private void UpdateCoreHint()
        {
            if (_coreHint == null) return;
            int manual = (int)AppConfig.GetDouble(ConfigSchema.GameCoreCount, 0);
            int phys = CpuTopology.PhysicalCoreCount;
            int threadsPerCore = CpuTopology.HasSMT ? 2 : 1;
            string prefix = string.IsNullOrEmpty(_coreResetNote) ? "" : _coreResetNote + "；";
            if (manual <= 0)
            {
                _coreHint.Text = prefix + $"自动：{CpuTopology.GetProfileDescription()}";
            }
            else if (manual < phys)
            {
                int bg = phys - manual;
                _coreHint.Text = prefix +
                    $"手动 {manual} 个物理核（{manual * threadsPerCore} 线程）给游戏，" +
                    $"后台保留 {bg} 个物理核（{bg * threadsPerCore} 线程）";
            }
            else
            {
                _coreHint.Text = prefix +
                    $"手动 {manual} 个物理核全部给游戏（后台只剩超线程兄弟线程，隔离不生效）";
            }
        }

        private void ScheduleSave()
        {
            if (_loading) return;
            _saveState.Text = "保存中…";
            _saveState.ForeColor = Theme.TextMuted;
            Header.PerformLayout();
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void SaveNow()
        {
            AppConfig.ForceSave();
            _saveState.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
            _saveState.ForeColor = Theme.Success;
            Header.PerformLayout();
        }

        // ═══════════ 诊断 ═══════════
        private string GetSystemInfo()
        {
            var uptime = DateTime.Now - AppEnvironment.StartedAt;
            var ctx = SchedulerCoordinator.GetLastContext();
            var level = SchedulerCoordinator.GetScheduleLevel();
            return
                $"系统      {Environment.OSVersion}  ·  .NET {Environment.Version}  ·  管理员 {(Program.HasAdminPrivileges ? "是" : "否")}\n" +
                $"处理器    {CpuTopology.GetProfileDescription()}\n" +
                $"运行      {(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分  ·  v{Constants.VERSION}\n" +
                $"数据      置信度 {(ctx?.DataConfidence ?? 0):F2}  ·  降级 {SchedulerDegradationEngine.GetLevelName(level)}\n" +
                $"模块      {UiToolkit.Plain(ModuleHealthMonitor.GetReport())}\n" +
                $"学习      {UiToolkit.Plain(IntelligenceEngine.GetStats())}\n" +
                $"          {UiToolkit.Plain(IntelligenceEngine.GetLearningStats())}\n" +
                $"预加载    {UiToolkit.Plain(UnifiedPreloader.GetStats())}\n" +
                $"限速      {UiToolkit.Plain(AutoJobManager.GetStats())}\n" +
                $"隔离      {InstructionSetOptimizer.GetIsolationStatus()}\n" +
                $"内存      {UiToolkit.Plain(MemoryReclaimer.GetStats())}  ·  {UiToolkit.Plain(JobObjectApi.GetStats())}";
        }
    }
}
