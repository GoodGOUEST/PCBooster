// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/MainForm.cs
// 主窗体（UI 重写）：扁平侧栏 + 内容区 + 侧栏底部状态
// 说明: 全面板手动排版（不依赖停靠顺序），AutoScaleMode=None，
// 尺寸全部由 Theme.S() 按 DPI 换算，缩放后不会错位。
// 保留: 托盘驻留 / 关闭拦截 / 隐私引导 / 页面定时器生命周期
// ============================================================
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PCBooster.Core;
using PCBooster.Infrastructure.Common;
using PCBooster.Infrastructure.Native;

namespace PCBooster.UI
{
    /// <summary>主窗体</summary>
    public class MainForm : Form
    {
        private readonly Panel _sidebar;
        private readonly Panel _content;
        private readonly SidebarFooter _footer;
        private readonly List<NavItem> _navItems = new();
        private readonly List<BoosterPage> _pages = new();
        private int _currentIndex = -1;
        private System.Windows.Forms.Timer _statusTimer;
        private NotifyIcon _trayIcon;
        private bool _realExit;

        public MainForm()
        {
            Theme.Initialize();
            AutoScaleMode = AutoScaleMode.None;
            Text = "PC Booster";
            Size = new Size(Theme.S(1360), Theme.S(880));
            MinimumSize = new Size(Theme.S(1180), Theme.S(760));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            DoubleBuffered = true;
            Icon = AppIcon.Get();

            if (!ForegroundWatcher.IsInstalled)
                ForegroundWatcher.Install(SchedulerCoordinator.HandleForeground);

            _sidebar = new Panel { BackColor = Theme.Surface };
            _sidebar.Paint += PaintSidebar;

            string[] nav = { "游戏", "总览", "进程", "规则", "智能", "日志", "设置" };
            for (int i = 0; i < nav.Length; i++)
            {
                int index = i;
                var item = new NavItem(nav[i]);
                item.Click += (s, e) => SwitchPage(index);
                _navItems.Add(item);
                _sidebar.Controls.Add(item);
            }

            _footer = new SidebarFooter();
            _sidebar.Controls.Add(_footer);

            _content = new Panel
            {
                BackColor = Theme.Bg,
                Padding = new Padding(Theme.S(28), Theme.S(20), Theme.S(28), Theme.S(16))
            };

            Controls.Add(_sidebar);
            Controls.Add(_content);

            InitializePages();
            InitializeStatus();
            InitializeTray();
            FormClosing += OnMainFormClosing;
            AppServices.RestartRequested += RestartAsAdmin;
            SwitchPage(0);
            ShowPrivacyGuideIfNeeded();
        }

        // ═══════════ 外观 ═══════════
        private void PaintSidebar(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Border, 1))
                g.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);

            var mark = new Rectangle(Theme.S(20), Theme.S(24), Theme.S(10), Theme.S(10));
            using (var brush = new SolidBrush(Theme.Accent)) g.FillRectangle(brush, mark);
            TextRenderer.DrawText(g, "PC Booster", Theme.FontCardTitle,
                new Point(Theme.S(38), Theme.S(18)), Theme.TextPrimary, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"v{Constants.VERSION}  ·  system tuner", Theme.FontSmall,
                new Point(Theme.S(20), Theme.S(42)), Theme.TextMuted, TextFormatFlags.NoPadding);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_sidebar == null || _content == null || _footer == null) return; // 构造期提前布局
            int sidebarWidth = Theme.S(240);
            _sidebar.SetBounds(0, 0, sidebarWidth, ClientSize.Height);
            _content.SetBounds(sidebarWidth, 0,
                System.Math.Max(0, ClientSize.Width - sidebarWidth), ClientSize.Height);

            int footerHeight = Theme.S(112);
            _footer.SetBounds(0, _sidebar.Height - footerHeight, sidebarWidth, footerHeight);
            for (int i = 0; i < _navItems.Count; i++)
                _navItems[i].SetBounds(Theme.S(10), Theme.S(96) + i * Theme.S(44),
                    sidebarWidth - Theme.S(20), Theme.S(40));
        }

        // ═══════════ 页面 ═══════════
        private void InitializePages()
        {
            _pages.Add(new GamePage());
            _pages.Add(new DashboardPage());
            _pages.Add(new SchedulerPage());
            _pages.Add(new RulesPage());
            _pages.Add(new AIPage());
            _pages.Add(new DecisionPage());
            _pages.Add(new AdvancedControlPage());
        }

        private void SwitchPage(int index)
        {
            if (index == _currentIndex || index < 0 || index >= _pages.Count) return;
            var previous = _currentIndex >= 0 ? _pages[_currentIndex] : null;
            previous?.OnPageHidden();
            _currentIndex = index;
            for (int i = 0; i < _navItems.Count; i++) _navItems[i].Active = i == index;

            _content.Controls.Clear();
            var page = _pages[index];
            page.Dock = DockStyle.Fill;
            _content.Controls.Add(page);
            page.OnPageShown();
        }

        // ═══════════ 状态 ═══════════
        private void InitializeStatus()
        {
            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (s, e) => UpdateStatus();
            UpdateStatus();
            _statusTimer.Start();
        }

        private void UpdateStatus()
        {
            var (cpu, mem) = Infrastructure.Cache.CacheManager.GetSystemState();
            _footer.Detail = $"CPU {cpu:F1}%    内存 {mem:F1}%\n周期 {SchedulerCoordinator.GetCycleCount()}";
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_statusTimer == null) return;
            if (Visible) _statusTimer.Start();
            else _statusTimer.Stop();
        }

        // ═══════════ 端口 / 托盘 ═══════════
        private void InitializeTray()
        {
            var menu = new ContextMenuStrip { Font = Theme.FontBody };
            menu.Items.Add("显示主窗口", null, (s, e) => RestoreWindow());
            menu.Items.Add("打开日志目录", null, (s, e) => OpenPath(AppPaths.LogDir));
            menu.Items.Add("打开配置目录", null, (s, e) => OpenPath(AppPaths.DataRoot));
            menu.Items.Add("立即还原全部优化", null, (s, e) =>
            {
                var (processes, jobs) = Core.BoosterActions.RestoreAll();
                _trayIcon?.ShowBalloonTip(3000, "PC Booster",
                    $"已还原 {processes} 个进程、{jobs} 个限速", ToolTipIcon.Info);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 PC Booster", null, (s, e) => { _realExit = true; Close(); });
            _trayIcon = new NotifyIcon
            {
                Icon = AppIcon.Get(),
                Text = "PC Booster",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreWindow();
        }

        /// <summary>用资源管理器打开目录（日志/配置）</summary>
        private static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLogger.Warning($"打开目录失败: {ex.Message}"); }
        }

        private void RestoreWindow()        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        /// <summary>以管理员身份重启（UAC 取消时保持当前实例）</summary>
        private void RestartAsAdmin()
        {
            if (AppServices.RestartElevated?.Invoke() == true)
            {
                _realExit = true;
                Close();
                return;
            }
            MessageBox.Show(this,
                "未能提权重启（可能取消了 UAC 提示）。\n当前仍以标准权限运行：工作集锁定与限速不可用。",
                "重启未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>第二个实例被双击时，把窗口拉回前台（托盘隐藏也能拉）</summary>
        internal void RestoreFromAnotherInstance()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((System.Action)RestoreWindow); } catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void ShowPrivacyGuideIfNeeded()
        {
            if (AppConfig.GetBool("PrivacyGuideShown", false)) return;
            var result = MessageBox.Show(this,
                "PC Booster 可通过学习您的软件使用习惯来预测接下来要用的程序，\n" +
                "并提前为其保留内存工作集（不会自行启动任何程序）。\n\n" +
                "• 所有数据仅保存在本机，永不上传\n" +
                "• 学习数据默认关闭，开启后才会记录\n" +
                "• 场景识别只在本机内存比对进程名，不落盘\n\n" +
                "是否开启智能学习功能？",
                "隐私与智能学习", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            AppConfig.EnableDataCollection = result == DialogResult.Yes;
            AppConfig.Set("PrivacyGuideShown", "True");
            AppConfig.ForceSave();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyDarkTitleBar(); // 部分 Win11 版本仅在窗口显示后才接受标题栏颜色
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDarkTitleBar();
        }

        /// <summary>深色标题栏：优先用 Win11 的沉浸式暗色 + 标题颜色，旧系统自动忽略</summary>
        private void ApplyDarkTitleBar()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int)); // 旧版编号
                int caption = Theme.Surface.R | (Theme.Surface.G << 8) | (Theme.Surface.B << 16);
                int text = Theme.TextPrimary.R | (Theme.TextPrimary.G << 8) | (Theme.TextPrimary.B << 16);
                int border = Theme.Surface.R | (Theme.Surface.G << 8) | (Theme.Surface.B << 16);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
            }
            catch (System.Exception ex) { PCBooster.Infrastructure.Common.AppLogger.DebugRateLimited("忽略异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_realExit && AppConfig.EnableTrayResident)
            {
                e.Cancel = true;
                Hide();
                // 第一次“关闭到托盘”时给一次气泡提示，
                // 否则用户会以为程序已退出（实际还在后台优化）。
                if (!AppConfig.GetBool("TrayHintShown", false))
                {
                    AppConfig.Set("TrayHintShown", "True");
                    AppConfig.ForceSave();
                    _trayIcon?.ShowBalloonTip(3000, "PC Booster 仍在后台运行",
                        "双击托盘图标可重新打开；右键可退出。", ToolTipIcon.Info);
                }
                return;
            }
            _statusTimer?.Stop();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }

        // ═══════════ 侧栏控件 ═══════════
        private sealed class NavItem : Control
        {
            private bool _active;
            private bool _hover;

            public NavItem(string text)
            {
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Surface;
                Font = Theme.FontBodyStrong;
                Cursor = Cursors.Hand;
            }

            public bool Active
            {
                get => _active;
                set
                {
                    if (_active == value) return;
                    _active = value;
                    Invalidate();
                }
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width, Height);
                if (_active || _hover)
                {
                    using var path = Theme.RoundedRectangle(rect, Theme.S(8));
                    using var brush = new SolidBrush(_active ? Theme.SurfaceHover : Color.FromArgb(120, 30, 36, 47));
                    g.FillPath(brush, path);
                }
                if (_active)
                {
                    var bar = new Rectangle(0, (Height - Theme.S(18)) / 2, Theme.S(3), Theme.S(18));
                    using var brush = new SolidBrush(Theme.Accent);
                    using var path = Theme.RoundedRectangle(bar, Theme.S(2));
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, Text, Font,
                    new Rectangle(Theme.S(16), 0, Math.Max(1, Width - Theme.S(20)), Height),
                    _active ? Theme.TextPrimary : Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private sealed class SidebarFooter : Panel
        {
            private string _detail = "";

            public SidebarFooter()
            {
                DoubleBuffered = true;
                BackColor = Theme.Surface;
            }

            public string Detail
            {
                get => _detail;
                set
                {
                    _detail = value;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var pen = new Pen(Theme.Border, 1))
                    g.DrawLine(pen, Theme.S(20), 0, Width - Theme.S(20), 0);
                int y = Theme.S(18);
                using (var dot = new SolidBrush(Theme.Success))
                    g.FillEllipse(dot, Theme.S(20), y + Theme.S(4), Theme.S(7), Theme.S(7));
                TextRenderer.DrawText(g, "运行中", Theme.FontSmall,
                    new Point(Theme.S(34), y), Theme.Success, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, _detail, Theme.FontSmall,
                    new Rectangle(Theme.S(20), y + Theme.S(24),
                        Math.Max(1, Width - Theme.S(30)), Theme.S(48)),
                    Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }
    }
}
