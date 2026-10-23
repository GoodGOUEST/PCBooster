// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/DashboardPage.cs
// 总览页（UI 重写）：四项指标 + 两张走势卡
// ============================================================
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PCBooster.Infrastructure.Cache;
using PCBooster.Infrastructure.Common;
using PCBooster.Intelligence;

namespace PCBooster.UI
{
    /// <summary>仪表盘页面</summary>
    public class DashboardPage : BoosterPage
    {
        private readonly CardGrid _metrics;
        private readonly CardGrid _charts;
        private readonly StatRow _cpu, _mem, _scene, _preload;
        private readonly ChartBox _cpuChart, _memChart;
        private readonly List<double> _cpuHistory = new();
        private readonly List<double> _memHistory = new();
        private const int ChartPoints = 60;

        public DashboardPage() : base(2000, "总览", "实时负载、当前场景与预加载效果。")
        {
            _metrics = new CardGrid(4) { AutoHeight = false, Stretch = true };
            _cpu = new StatRow("0%", "近 2 秒平均", Theme.Accent);
            _mem = new StatRow("0%", "可用物理内存", Theme.Success);
            _scene = new StatRow("常规", "前台决定场景", Theme.Accent);
            _preload = new StatRow("—", "预加载命中率", Theme.Success);
            _metrics.Add(new Card("CPU 使用率")).Add(_cpu);
            _metrics.Add(new Card("内存可用")).Add(_mem);
            _metrics.Add(new Card("当前场景")).Add(_scene);
            _metrics.Add(new Card("预加载成功率")).Add(_preload);

            _charts = new CardGrid(2) { AutoHeight = false, Stretch = true };
            _cpuChart = new ChartBox();
            _charts.Add(new Card("CPU 走势", "最近 60 个采样点")).AddFill(_cpuChart);
            _memChart = new ChartBox();
            _charts.Add(new Card("内存可用走势", "纵轴 0–100%")).AddFill(_memChart);

            _cpuChart.Paint += (s, e) => DrawChart(e.Graphics, _cpuHistory, Theme.Accent, _cpuChart);
            _memChart.Paint += (s, e) => DrawChart(e.Graphics, _memHistory, Theme.Success, _memChart);

            Content.Controls.Add(_metrics);
            Content.Controls.Add(_charts);
        }

        protected override void LayoutContent(Size size)
        {
            int metricsHeight = Math.Max(Theme.S(124), _metrics.PreferredHeight(size.Width));
            _metrics.SetBounds(0, 0, size.Width, metricsHeight);
            int top = metricsHeight + Theme.S(12);
            _charts.SetBounds(0, top, size.Width, System.Math.Max(Theme.S(160), size.Height - top));
        }

        protected override void OnRefresh()
        {
            var (cpu, mem) = CacheManager.GetSystemState();
            _cpu.Value = $"{cpu:F1}%";
            _mem.Value = $"{mem:F1}%";
            _scene.Value = SceneName(IntelligenceEngine.GetScene());
            _preload.Value = $"{Execution.Preload.UnifiedPreloader.GetHitRate():P0}";

            _cpuHistory.Add(cpu);
            _memHistory.Add(mem);
            while (_cpuHistory.Count > ChartPoints) _cpuHistory.RemoveAt(0);
            while (_memHistory.Count > ChartPoints) _memHistory.RemoveAt(0);
            _cpuChart.Invalidate();
            _memChart.Invalidate();
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

        private sealed class ChartBox : Panel
        {
            public ChartBox()
            {
                DoubleBuffered = true;
                BackColor = Theme.SurfaceAlt;
            }
        }

        private static void DrawChart(Graphics g, List<double> data, Color color, ChartBox host)
        {
            if (host.Width <= 4 || host.Height <= 4) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int gutter = Theme.S(36);
            int labelH = Theme.FontSmall.Height;
            int w = host.Width - gutter;
            int top = labelH / 2 + 1;
            int bottom = host.Height - labelH / 2 - 1;
            int h = bottom - top;
            if (w <= 8 || h <= 8) return;

            using (var grid = new Pen(Color.FromArgb(90, Theme.Border), Theme.Sf(1f)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = bottom - (int)(h * i / 4.0);
                    g.DrawLine(grid, gutter, y, host.Width, y);
                    TextRenderer.DrawText(g, (i * 25).ToString(), Theme.FontSmall,
                        new Rectangle(0, y - labelH / 2, gutter - Theme.S(8), labelH),
                        Theme.TextMuted,
                        TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                }
            }
            using (var vgrid = new Pen(Color.FromArgb(70, Theme.Border), Theme.Sf(1f)))
            {
                for (int i = 1; i < 6; i++)
                {
                    int x = gutter + (int)((w - 1) * i / 6.0);
                    g.DrawLine(vgrid, x, top, x, bottom);
                }
            }
            if (data.Count < 2)
            {
                TextRenderer.DrawText(g, "采集中…", Theme.FontSmall,
                    new Rectangle(gutter, top, w, h), Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var points = new PointF[data.Count];
            float denom = System.Math.Max(1, data.Count - 1);
            for (int i = 0; i < data.Count; i++)
                points[i] = new PointF(gutter + (float)i / denom * (w - 1),
                    bottom - (float)(data[i] / 100.0) * h);

            using var path = new GraphicsPath();
            path.AddLines(points);
            path.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y, points[points.Length - 1].X, bottom);
            path.AddLine(points[0].X, bottom, points[0].X, points[0].Y);
            path.CloseFigure();
            using var fill = new LinearGradientBrush(new Rectangle(0, 0, System.Math.Max(1, w), System.Math.Max(1, h)),
                Color.FromArgb(56, color), Color.FromArgb(4, color), LinearGradientMode.Vertical);
            g.FillPath(fill, path);
            using var linePen = new Pen(color, Theme.Sf(2f)) { LineJoin = LineJoin.Round };
            g.DrawLines(linePen, points);
        }
    }
}
