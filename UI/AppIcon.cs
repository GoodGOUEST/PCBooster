// ============================================================
// PC Booster v3.6.8
// UI/AppIcon.cs
// 运行时生成应用图标（任务栏 / 托盘 / 标题栏）
// 说明: 不额外携带二进制 .ico，避免资源丢失与签名问题；
//       一枚强调色圆角方块 + 深色 "P"，16/32 px 下都能认出来。
// ============================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCBooster.UI
{
    /// <summary>运行时图标工厂</summary>
    internal static class AppIcon
    {
        private static Icon _icon;

        public static Icon Get()
        {
            if (_icon != null) return _icon;
            const int size = 32;
            using var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var path = Theme.RoundedRectangle(new Rectangle(0, 0, size, size), size / 5))
                using (var brush = new LinearGradientBrush(
                           new Rectangle(0, 0, size, size),
                           Color.FromArgb(140, 190, 255),
                           Color.FromArgb(84, 132, 220),
                           LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
                using (var font = new Font("Segoe UI", size * 0.54f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat
                       {
                           Alignment = StringAlignment.Center,
                           LineAlignment = StringAlignment.Center
                       })
                using (var ink = new SolidBrush(Theme.AccentInk))
                {
                    g.DrawString("P", font, ink, new RectangleF(0, -size * 0.03f, size, size), format);
                }
            }
            // 句柄由 Icon 持有，随进程存活；应用退出时系统回收
            _icon = Icon.FromHandle(bitmap.GetHicon());
            return _icon;
        }
    }
}
