// ============================================================
// PC Booster v3.6.8
// UI/Theme.cs
// 设计令牌 + DPI 缩放（UI 重写）
// 说明: 所有尺寸统一走 S()/Sf()，字体按当前 DPI 一次性建立。
//       这样 125% / 150% / 200% 缩放下文字与控件同步放大，
//       不会出现“字比框大、框压字”的错位（旧版硬编码像素的正因）。
// ============================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCBooster.UI
{
    /// <summary>主题令牌与绘制工具（UI 层共享，不依赖任何窗体）</summary>
    public static class Theme
    {
        // ── 颜色 ──────────────────────────────────────────────
        public static readonly Color Bg = Color.FromArgb(10, 12, 16);
        public static readonly Color Surface = Color.FromArgb(17, 20, 26);
        public static readonly Color SurfaceAlt = Color.FromArgb(22, 26, 34);
        public static readonly Color SurfaceHover = Color.FromArgb(30, 36, 47);
        public static readonly Color Border = Color.FromArgb(36, 43, 54);
        public static readonly Color BorderStrong = Color.FromArgb(52, 62, 78);
        public static readonly Color TextPrimary = Color.FromArgb(231, 238, 247);
        public static readonly Color TextSecondary = Color.FromArgb(152, 164, 179);
        public static readonly Color TextMuted = Color.FromArgb(102, 112, 125);
        public static readonly Color Accent = Color.FromArgb(110, 168, 254);
        public static readonly Color AccentInk = Color.FromArgb(11, 18, 32);
        public static readonly Color AccentSoft = Color.FromArgb(24, 35, 55);
        public static readonly Color Success = Color.FromArgb(74, 222, 128);
        public static readonly Color Warning = Color.FromArgb(251, 191, 36);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);

        // ── DPI ───────────────────────────────────────────────
        private static bool _ready;
        private static int _dpi = 96;

        /// <summary>逻辑像素 → 设备像素（整数）</summary>
        public static int S(int value)
        {
            Ensure();
            return (int)Math.Round(value * _dpi / 96.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>逻辑像素 → 设备像素（浮点，用于字体大小与画笔宽度）</summary>
        public static float Sf(float value)
        {
            Ensure();
            return (float)(value * _dpi / 96.0);
        }

        /// <summary>幂等初始化；测试可用 PCBOOSTER_UI_DPI 环境变量强制指定</summary>
        public static void Initialize(int dpi = 0)
        {
            if (_ready && dpi <= 0) return;
            _ready = true;
            _dpi = dpi > 0 ? dpi : DetectDpi();
            float k = _dpi / 96f;
            FontDisplay = new Font("Segoe UI", 20f * k, FontStyle.Bold, GraphicsUnit.Point);
            FontH1 = new Font("Segoe UI", 15f * k, FontStyle.Bold, GraphicsUnit.Point);
            FontCardTitle = new Font("Segoe UI", 11f * k, FontStyle.Bold, GraphicsUnit.Point);
            FontBody = new Font("Segoe UI", 9.5f * k, GraphicsUnit.Point);
            FontBodyStrong = new Font("Segoe UI", 9.5f * k, FontStyle.Bold, GraphicsUnit.Point);
            FontSmall = new Font("Segoe UI", 8.5f * k, GraphicsUnit.Point);
            FontMono = new Font("Consolas", 9f * k, GraphicsUnit.Point);
        }

        private static void Ensure()
        {
            if (!_ready) Initialize(0);
        }

        private static int DetectDpi()
        {
            string forced = Environment.GetEnvironmentVariable("PCBOOSTER_UI_DPI");
            if (int.TryParse(forced, out int value) && value >= 96 && value <= 480) return value;
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                int dpi = (int)Math.Round(g.DpiX);
                return dpi >= 96 ? dpi : 96;
            }
            catch { return 96; }
        }

        // ── 字体（初始化时按 DPI 建立，勿 Dispose）─────────────
        public static Font FontDisplay { get; private set; }
        public static Font FontH1 { get; private set; }
        public static Font FontCardTitle { get; private set; }
        public static Font FontBody { get; private set; }
        public static Font FontBodyStrong { get; private set; }
        public static Font FontSmall { get; private set; }
        public static Font FontMono { get; private set; }

        /// <summary>圆角矩形路径（调用方负责 Dispose）</summary>
        public static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            var arc = new Rectangle(rect.X, rect.Y, d, d);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
