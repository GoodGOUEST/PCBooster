// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/SchedulerPage.cs
// 进程页（UI 重写）：工作集前 100 进程 + 右键分类/优先级/游戏标记
// ============================================================
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;
using PCBooster.Intelligence;

namespace PCBooster.UI
{
    /// <summary>进程页</summary>
    public class SchedulerPage : BoosterPage
    {
        private readonly DataList _list;
        private readonly Card _card;
        private readonly Toolbar _bar;
        private readonly Label _cycle;
        private readonly Label _guard;

        public SchedulerPage() : base(2000, "进程", "按工作集排序的前 100 个进程；右键可指定分类、优先级或游戏标记。")
        {
            _cycle = new Label
            {
                Text = "周期 0",
                AutoSize = false,
                Width = Theme.S(140),
                Height = Theme.S(20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall,
                BackColor = Theme.Bg
            };
            var refresh = UiToolkit.CreateButton("立即刷新");
            refresh.Click += (s, e) => _ = RefreshListAsync();

            // 日常负载保护状态（编译/办公/浏览器）——直接展示为何某些进程不被处理
            _guard = new Label
            {
                Text = "— 无特殊保护",
                AutoSize = false,
                Width = Theme.S(360),
                Height = Theme.S(20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall,
                BackColor = Theme.Bg
            };

            _bar = new Toolbar();
            _bar.AddLeft(refresh).AddLeft(_guard).AddRight(_cycle);

            _card = new Card("进程列表", "保护中的进程不会被限速或改写亲和性");
            _list = new DataList(
                ("PID", 80), ("进程名", 240), ("工作集", 110), ("分类", 150), ("保护", 100));
            _list.ContextMenuStrip = BuildClassMenu();
            _card.AddFill(_list);

            Content.Controls.Add(_bar);
            Content.Controls.Add(_card);
        }

        protected override void LayoutContent(Size size)
        {
            _bar.SetBounds(0, 0, size.Width, _bar.Height);
            int top = _bar.Bottom;
            _card.SetBounds(0, top, size.Width, Math.Max(Theme.S(120), size.Height - top));
        }

        /// <summary>右键给选中进程指定分类（写入 DataCollector.UserOverride）</summary>
        private ContextMenuStrip BuildClassMenu()
        {
            var menu = new ContextMenuStrip { Font = Theme.FontBody };
            menu.Items.Add("标记为大型重载", null, (s, e) => SetSelectedClass("HeavyCompute"));
            menu.Items.Add("标记为内存大户", null, (s, e) => SetSelectedClass("HeavyMemory"));
            menu.Items.Add("标记为交互程序", null, (s, e) => SetSelectedClass("Interactive"));
            menu.Items.Add("标记为后台程序", null, (s, e) => SetSelectedClass("Background"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清除标记（回到自动分类）", null, (s, e) => SetSelectedClass(""));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("标记为游戏", null, (s, e) => MarkGame(true));
            menu.Items.Add("取消游戏标记", null, (s, e) => MarkGame(false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("降为后台优先级", null, (s, e) =>
                SetPriorityForSelected(System.Diagnostics.ProcessPriorityClass.BelowNormal));
            menu.Items.Add("恢复正常优先级", null, (s, e) =>
                SetPriorityForSelected(System.Diagnostics.ProcessPriorityClass.Normal));
            menu.Items.Add("解除 Job 限制", null, (s, e) => ReleaseSelectedJob());
            return menu;
        }

        private string SelectedName() => _list.FirstSelected?.Cell(1) ?? "";

        private void MarkGame(bool mark)
        {
            var name = SelectedName();
            if (string.IsNullOrEmpty(name)) return;
            if (mark) GameCatalog.Mark(name, name);
            else GameCatalog.Unmark(name);
            _ = RefreshListAsync();
        }

        private void SetPriorityForSelected(System.Diagnostics.ProcessPriorityClass priority)
        {
            var row = _list.FirstSelected;
            if (row == null) return;
            if (int.TryParse(row.Key, out int pid)) ProcessSafeHelper.SetPriority(pid, priority);
            _ = RefreshListAsync();
        }

        private void ReleaseSelectedJob()
        {
            var row = _list.FirstSelected;
            if (row == null) return;
            if (int.TryParse(row.Key, out int pid))
                Execution.Jobs.JobObjectApi.ReleaseJob(pid);
            _ = RefreshListAsync();
        }

        private void SetSelectedClass(string cls)
        {
            var name = SelectedName();
            if (string.IsNullOrEmpty(name)) return;
            IntelligenceEngine.SetUserOverride(name, cls);
            _ = RefreshListAsync();
        }

        protected override void OnRefresh() => _ = RefreshListAsync();

        /// <summary>包裹异常（fire-and-forget 异常会成为未观察任务异常）</summary>
        private async Task RefreshListAsync()
        {
            try { await RefreshListCoreAsync(); }
            catch (System.Exception ex) { AppLogger.Debug($"调度页刷新异常: {ex.Message}"); }
        }

        /// <summary>后台采样 + 活体特征四级漏斗 + 工作集降序</summary>
        private async Task RefreshListCoreAsync()
        {
            var rows = await Task.Run(() =>
            {
                var snapshot = NativeApi.GetSnapshotCached();
                int fgPid = NativeApi.GetForegroundPid();
                return snapshot
                    .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                    .OrderByDescending(p => p.WorkingSet64)
                    .Take(100)
                    .Select(p => new
                    {
                        p.Id,
                        p.ProcessName,
                        WorkingSet = p.WorkingSet64,
                        Class = IntelligenceEngine.Classify(p.ProcessName,
                            p.WorkingSet64, NativeApi.GetProcessCpuCached(p.Id), p.Id == fgPid),
                        Protected = Execution.Preload.PreloadProtection.IsProtected(p.Id)
                    }).ToList();
            });
            if (IsDisposed) return;

            var data = new List<DataRow>(rows.Count);
            foreach (var row in rows)
                data.Add(new DataRow(
                    row.Id.ToString(),
                    row.Id.ToString(),
                    row.ProcessName,
                    $"{row.WorkingSet / 1024 / 1024} MB",
                    IntelligenceEngine.GetClassName(row.Class),
                    row.Protected ? "保护中" : "—"));
            _list.SetRows(data);

            _cycle.Text = $"周期 {Core.SchedulerCoordinator.GetCycleCount()}";
            _guard.Text = Execution.Work.WorkloadGuard.GetStatus();
            _bar.PerformLayout();
        }
    }
}
