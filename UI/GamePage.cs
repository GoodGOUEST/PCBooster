// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/GamePage.cs
// 游戏页（UI 重写）：状态优先、操作次之、列表最后
// 保留：手动目录 / 移除 / 一键还原 / 扫描
// ============================================================
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PCBooster.Core;
using PCBooster.Execution.Jobs;
using PCBooster.Execution.Power;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Infrastructure.Profiling;
using PCBooster.Intelligence;

namespace PCBooster.UI
{
    /// <summary>游戏主页</summary>
    public class GamePage : BoosterPage
    {
        private readonly CardGrid _hero;
        private readonly Toolbar _bar;
        private readonly Card _listCard;
        private readonly DataList _list;
        private readonly StatRow _scene, _power, _self;
        private readonly Label _hint;

        public GamePage() : base(2000, "游戏", "自动识别游戏，只优化游戏相关进程；不注入、不碰反作弊。")
        {
            _hero = new CardGrid(3) { AutoHeight = false, Stretch = true };

            _scene = new StatRow("—", "场景 常规", Theme.TextSecondary);
            _hero.Add(new Card("当前状态")).Add(_scene);

            _power = new StatRow("—", "频率 自动调节", Theme.TextSecondary);
            _hero.Add(new Card("电源与频率")).Add(_power);

            _self = new StatRow("—", "内存 — MB", Theme.TextSecondary);
            _hero.Add(new Card("本工具自耗")).Add(_self);

            _bar = new Toolbar();
            var restore = UiToolkit.CreatePrimaryButton("一键还原");
            restore.Click += (s, e) => RestoreAll();
            var scan = UiToolkit.CreateButton("扫描游戏库");
            scan.Click += (s, e) =>
            {
                GameCatalog.StartScan(force: true);
                _hint.Text = "扫描中…";
            };
            var add = UiToolkit.CreateButton("添加目录");
            add.Click += (s, e) => AddGameFolder();
            var remove = UiToolkit.CreateButton("移除选中");
            remove.Click += (s, e) => RemoveSelected();
            var refresh = UiToolkit.CreateButton("刷新");
            refresh.Click += (s, e) => OnRefresh();
            _hint = new Label
            {
                Text = "—", AutoSize = true, ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall, BackColor = Theme.Bg
            };
            _bar.AddLeft(restore).AddLeft(scan).AddLeft(add).AddLeft(remove).AddLeft(refresh).AddRight(_hint);

            _listCard = new Card("已识别的游戏");
            _list = new DataList(("可执行文件", 220), ("游戏名称", 380), ("来源", 100))
            {
                MultiSelect = true
            };
            _listCard.AddFill(_list);

            Content.Controls.Add(_hero);
            Content.Controls.Add(_bar);
            Content.Controls.Add(_listCard);
        }

        protected override void LayoutContent(Size size)
        {
            int heroHeight = Math.Max(Theme.S(150), _hero.PreferredHeight(size.Width)) + Theme.S(4);
            int gap = Theme.S(12);
            _hero.SetBounds(0, 0, size.Width, heroHeight);
            _bar.SetBounds(0, heroHeight + gap, size.Width, _bar.Height);
            int listTop = _bar.Bottom;
            _listCard.SetBounds(0, listTop, size.Width, Math.Max(Theme.S(120), size.Height - listTop));
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            OnRefresh();
        }

        protected override void OnRefresh()
        {
            if (IsDisposed) return;
            try
            {
                string scene = IntelligenceEngine.GetScene() ?? "general";
                bool gaming = string.Equals(scene, Constants.SCENE_GAMING, StringComparison.OrdinalIgnoreCase);
                bool gamingBg = string.Equals(scene, Constants.SCENE_GAMING_BACKGROUND, StringComparison.OrdinalIgnoreCase);
                _scene.Value = gaming ? "游戏中" : gamingBg ? "游戏后台" : "非游戏场景";
                _scene.ValueColor = gaming ? Theme.Success : gamingBg ? Theme.Warning : Theme.TextSecondary;

                var ctx = SchedulerCoordinator.GetLastContext();
                _scene.Detail = $"场景 {SceneName(scene)}  ·  置信度 {(ctx?.DataConfidence ?? 0):F2}\n" +
                                $"降级 {SchedulerDegradationEngine.GetLevelName(SchedulerCoordinator.GetScheduleLevel())}";
                if (gamingBg)
                    _scene.Detail += "\n游戏在后台：已释放锁频，保留核心隔离";

                var powerState = PowerSaver.GetCurrentState();
                bool locked = FrequencyManager.IsLocked;
                // 这里原来显示 PowerSaver 的内部枚举（“活跃”），用户看不懂。
                // 改成真实的 Windows 电源方案；锁频时直接写“高性能”。
                _power.Value = locked ? "高性能" : NativeApi.GetActiveSchemeName();
                _power.ValueColor = locked ? Theme.Success : Theme.TextSecondary;
                _power.Detail = $"频率 {(locked ? "已锁定最高频" : string.IsNullOrEmpty(FrequencyManager.LastLockError) ? "自动调节" : "未锁定（" + FrequencyManager.LastLockError + "）")}  ·  限速 {AutoJobManager.AutoLimitedCount} 个\n" +
                                $"隔离 {InstructionSetOptimizer.GetIsolationStatus()}";
                if (powerState == PowerSaver.PowerState.DeepSleep)
                    _power.Detail += "\n节能：后台优化已暂停";

                double cpu = NativeApi.GetProcessCpuCached(Environment.ProcessId);
                long wsMb = 0;
                try
                {
                    using var self = System.Diagnostics.Process.GetCurrentProcess();
                    wsMb = self.WorkingSet64 / 1024 / 1024;
                }
                catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                _self.Value = $"{cpu:F1}%";
                _self.Detail = $"内存 {wsMb} MB\n目标 <0.3% / <80MB";

                int total = GameCatalog.Count;
                if (GameCatalog.IsScanning) _hint.Text = "扫描中…";
                else if (GameCatalog.IsReady)
                    _hint.Text = $"已识别 {total} 个" +
                                 (GameCatalog.UserCount > 0 ? $"（手动 {GameCatalog.UserCount}）" : "");
                else _hint.Text = "尚未扫描：点“扫描游戏库”或“添加目录”";

                RefreshGameList();
                // 文本行高变化后重新分配卡片与列表高度
                LayoutContent(Content.ClientSize);
            }
            catch (Exception ex) { AppLogger.Debug($"游戏页刷新异常: {ex.Message}"); }
        }

        private static string SceneName(string scene) => scene switch
        {
            Constants.SCENE_GAMING => "游戏",
            Constants.SCENE_GAMING_BACKGROUND => "游戏后台",
            Constants.SCENE_VIDEO => "影音",
            Constants.SCENE_DESIGN => "设计",
            Constants.SCENE_DEVELOPMENT => "开发",
            Constants.SCENE_MEETING => "会议",
            Constants.SCENE_OFFICE => "办公",
            Constants.SCENE_BROWSING => "浏览",
            _ => "常规"
        };

        private void RefreshGameList()
        {
            var rows = GameCatalog.Snapshot(200);
            var data = new List<DataRow>(rows.Count);
            foreach (var (exe, game) in rows)
                data.Add(new DataRow(exe, exe, game, GameCatalog.IsUserMarked(exe) ? "手动" : "Steam/自动"));
            _list.SetRows(data);
        }

        // ═══════════ 手动添加游戏目录 ═══════════
        private void AddGameFolder()
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "选择游戏安装目录（程序会自动列出其中的可执行文件）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (fbd.ShowDialog(FindForm()) != DialogResult.OK) return;

            string folder = fbd.SelectedPath;
            var exes = GameCatalog.FindExecutables(folder);
            if (exes.Count == 0)
            {
                MessageBox.Show(this,
                    "该目录下没有找到可执行文件。\n\n" +
                    "请选择游戏主程序所在目录：最多向下 3 层子目录，\n" +
                    "redist / support 等辅助目录会被跳过。",
                    "未找到可执行文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string gameName;
            try { gameName = new DirectoryInfo(folder).Name; } catch { gameName = "游戏"; }

            var picked = PickExecutables(folder, exes, ref gameName);
            if (picked == null || picked.Count == 0) return;

            int added = GameCatalog.AddUserGames(folder, picked, gameName);
            OnRefresh();
            MessageBox.Show(this,
                $"已添加 {added} 个可执行文件：\n" +
                string.Join("\n", picked.Select(p => Path.GetFileName(p))) +
                $"\n\n游戏名称：{gameName}\n之后启动这些程序会被识别为“游戏场景”。",
                "添加成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private List<string> PickExecutables(string folder, List<string> exes, ref string gameName)
        {
            using var dlg = new Form
            {
                Text = "添加游戏目录",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                BackColor = Theme.Bg, ForeColor = Theme.TextPrimary,
                Font = Theme.FontBody,
                AutoScaleMode = AutoScaleMode.None,
                ClientSize = new Size(Theme.S(560), Theme.S(460))
            };

            dlg.Controls.Add(new Label
            {
                Text = "勾选要识别为游戏的可执行文件（一般只勾主程序）：",
                Location = new Point(Theme.S(20), Theme.S(16)), AutoSize = true,
                ForeColor = Theme.TextSecondary
            });

            var list = new CheckedListBox
            {
                Location = new Point(Theme.S(20), Theme.S(44)),
                Size = new Size(Theme.S(520), Theme.S(280)),
                CheckOnClick = true, IntegralHeight = false,
                BackColor = Theme.Surface, ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            for (int i = 0; i < exes.Count; i++)
            {
                string rel = exes[i];
                try { rel = Path.GetRelativePath(folder, exes[i]); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
                list.Items.Add($"{Path.GetFileName(exes[i])}    ({rel}, {SizeMb(exes[i])} MB)", i == 0);
            }
            dlg.Controls.Add(list);
            list.HandleCreated += (s, e) => DarkScrollbars.Apply(list.Handle);

            dlg.Controls.Add(new Label
            {
                Text = "游戏名称：", Location = new Point(Theme.S(20), Theme.S(336)), AutoSize = true,
                ForeColor = Theme.TextSecondary
            });
            var txtName = new TextBox
            {
                Location = new Point(Theme.S(96), Theme.S(332)),
                Size = new Size(Theme.S(444), Theme.S(26)),
                Text = gameName, BackColor = Theme.Surface, ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle
            };
            dlg.Controls.Add(txtName);

            var btnOk = UiToolkit.CreatePrimaryButton("添加");
            btnOk.Location = new Point(Theme.S(330), Theme.S(388));
            btnOk.DialogResult = DialogResult.OK;
            var btnCancel = UiToolkit.CreateButton("取消");
            btnCancel.Location = new Point(Theme.S(436), Theme.S(388));
            btnCancel.DialogResult = DialogResult.Cancel;
            dlg.Controls.Add(btnOk);
            dlg.Controls.Add(btnCancel);
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return null;

            var name = txtName.Text.Trim();
            if (name.Length > 0) gameName = name;

            var picked = new List<string>();
            foreach (int idx in list.CheckedIndices)
                if (idx >= 0 && idx < exes.Count) picked.Add(exes[idx]);

            if (picked.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个可执行文件。", "未选择",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return picked;
        }

        private static long SizeMb(string path)
        {
            try { return new FileInfo(path).Length / 1024 / 1024; } catch { return 0; }
        }

        private void RemoveSelected()
        {
            var selected = _list.SelectedRows;
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请先在列表中选中要移除的条目。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var names = selected.Select(r => r.Key).ToList();
            string what = names.Count == 1 ? "「" + names[0] + "」" : names.Count + " 个条目";
            if (MessageBox.Show(this,
                    $"确定移除{what}的游戏标记？\n\n" +
                    "• 手动添加的条目：从用户配置中删除\n" +
                    "• Steam 扫描的条目：加入排除表，重新扫描不会再加回来\n\n" +
                    "随时可用“添加目录”重新加入。",
                    "确认移除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            GameCatalog.RemoveEntries(names);
            OnRefresh();
        }

        private void RestoreAll()
        {
            var (processes, jobs) = BoosterActions.RestoreAll();
            MessageBox.Show(
                $"已还原：\n• {processes} 个进程的优先级 / 亲和性\n• {jobs} 个 Job 限速\n• 后台核心隔离、电源方案与核心停泊\n\n引擎会在下次决策时重新应用策略。",
                "还原完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OnRefresh();
        }
    }
}
