// ============================================================
// PC Booster v3.6.8
// UI/UiToolkit.cs
// 控件与布局工具箱（UI 重写）
// 设计要点:
//   1) 卡片内的每一行都实现 IRow，按文字实测高度上报尺寸 —— 不再用绝对坐标，
//      中文换行、DPI 缩放、窗口改变宽度时都不会重叠。
//   2) 尺寸一律走 Theme.S()/Sf()；文字度量统一 TextRenderer，与绘制一致。
//   3) 列表/日志等滚动区使用 ScrollHost 自绘细滚动条，避免 WinForms 原生
//      白色滚动条在深色界面里“破相”。
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PCBooster.UI
{
    /// <summary>卡片内可自行上报高度的行</summary>
    public interface IRow
    {
        int MeasureHeight(int width);
    }

    // ══════════════════════════════════════════════════════════
    //  行 1：只读文本块（自动换行）
    // ══════════════════════════════════════════════════════════
    public sealed class TextBlock : Panel, IRow
    {
        private readonly Font _font;

        public TextBlock(string text, Font font = null, Color? color = null)
        {
            Text = text ?? "";
            _font = font ?? Theme.FontBody;
            ForeColor = color ?? Theme.TextSecondary;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public int MeasureHeight(int width)
        {
            int inner = Math.Max(Theme.S(40), width - Theme.S(24));
            return UiToolkit.MeasureParagraph(Text, _font, inner) + Theme.S(14);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
            // 行高由文字实测得出：卡片与栅格都要重排，否则内容变化后会被裁切
            Parent?.PerformLayout();
            Parent?.Parent?.PerformLayout();
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Text, _font,
                new Rectangle(Theme.S(12), Theme.S(7),
                    Math.Max(1, Width - Theme.S(24)), Math.Max(1, Height - Theme.S(14))),
                ForeColor, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Top);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  行 2：大号数值（状态卡用）
    // ══════════════════════════════════════════════════════════
    public sealed class StatRow : Panel, IRow
    {
        private string _value;
        private string _detail;
        private Color _valueColor;

        public StatRow(string value, string detail = "", Color? valueColor = null)
        {
            _value = value ?? "";
            _detail = detail ?? "";
            _valueColor = valueColor ?? Theme.TextPrimary;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public string Value
        {
            get => _value;
            set
            {
                _value = value ?? "";
                Invalidate();
            }
        }

        public string Detail
        {
            get => _detail;
            set
            {
                _detail = value ?? "";
                Invalidate();
                // 行高按实测文字计算，内容变化后必须重新排布，否则多出的行会被裁掉
                Parent?.PerformLayout();
                Parent?.Parent?.PerformLayout();
            }
        }

        public Color ValueColor
        {
            get => _valueColor;
            set { _valueColor = value; Invalidate(); }
        }

        public int MeasureHeight(int width)
        {
            int inner = Math.Max(Theme.S(40), width - Theme.S(24));
            int h = UiToolkit.MeasureTextHeight(_value, Theme.FontH1, inner) + Theme.S(2);
            if (!string.IsNullOrEmpty(_detail))
            {
                h += Theme.S(4) + UiToolkit.MeasureParagraph(_detail, Theme.FontSmall, inner) + Theme.S(4);
            }
            return h + Theme.S(18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int y = Theme.S(10);
            TextRenderer.DrawText(e.Graphics, _value, Theme.FontH1,
                new Rectangle(Theme.S(12), y, Math.Max(1, Width - Theme.S(24)), Theme.FontH1.Height + Theme.S(2)),
                _valueColor, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += Theme.FontH1.Height + Theme.S(4);
            if (!string.IsNullOrEmpty(_detail))
                TextRenderer.DrawText(e.Graphics, _detail, Theme.FontSmall,
                    new Rectangle(Theme.S(12), y, Math.Max(1, Width - Theme.S(24)),
                        Math.Max(1, Height - y - Theme.S(4))),
                    Theme.TextSecondary,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Top);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  行 3：开关行（整行可点击，右下角药丸拨杆）
    // ══════════════════════════════════════════════════════════
    public sealed class SwitchRow : Panel, IRow
    {
        private readonly string _title;
        private readonly string _hint;
        private readonly Action<bool> _changed;
        private bool _on;
        private bool _hover;

        public SwitchRow(string title, string hint, bool value, Action<bool> changed = null)
        {
            _title = title ?? "";
            _hint = hint;
            _on = value;
            _changed = changed;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        /// <summary>程序化赋值（raise=false 时不触发回调）</summary>
        public void SetChecked(bool value, bool raise = false)
        {
            if (_on == value) { Invalidate(); return; }
            _on = value;
            Invalidate();
            if (raise && _changed != null) _changed(_on);
        }

        public int MeasureHeight(int width)
        {
            int textW = Math.Max(Theme.S(40), width - Theme.S(24) - UiToolkit.SwitchWidth - Theme.S(16));
            int h = UiToolkit.MeasureTextHeight(_title, Theme.FontBodyStrong, textW);
            if (!string.IsNullOrEmpty(_hint))
                h += Theme.S(2) + UiToolkit.MeasureParagraph(_hint, Theme.FontSmall, textW);
            return Math.Max(Theme.S(50), h + Theme.S(20));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (_hover)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Theme.RoundedRectangle(
                    new Rectangle(Theme.S(2), Theme.S(2), Math.Max(1, Width - Theme.S(4)),
                        Math.Max(1, Height - Theme.S(4))), Theme.S(8));
                using var brush = new SolidBrush(Theme.SurfaceHover);
                g.FillPath(brush, path);
            }
            int x = Theme.S(12);
            int textW = Math.Max(Theme.S(40), Width - Theme.S(24) - UiToolkit.SwitchWidth - Theme.S(16));
            UiToolkit.DrawTitleAndHint(g, _title, _hint, x, Theme.S(10), textW, out int titleH);
            int sw = UiToolkit.SwitchWidth, sh = UiToolkit.SwitchHeight;
            int sy = Theme.S(10) + Math.Max(0, (titleH - sh) / 2);
            UiToolkit.DrawSwitch(g, Width - Theme.S(12) - sw, sy, sw, sh, _on, _hover);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _on = !_on;
                Invalidate();
                _changed?.Invoke(_on);
            }
            base.OnMouseUp(e);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  行 4：数值行（右侧数字框）
    // ══════════════════════════════════════════════════════════
    public sealed class NumberRow : Panel, IRow
    {
        private readonly string _title;
        private readonly string _hint;
        private readonly NumberBox _box;

        public NumberRow(string title, string hint, decimal min, decimal max, decimal value,
            int decimals = 0, decimal step = 1m, Action<decimal> changed = null)
        {
            _title = title ?? "";
            _hint = hint;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            SetStyle(ControlStyles.ResizeRedraw, true);
            _box = new NumberBox(min, max, value, decimals, step);
            if (changed != null) _box.ValueChanged += (s, e) => changed(_box.Value);
            Controls.Add(_box);
        }

        public decimal Value
        {
            get => _box.Value;
            set => _box.Value = value;
        }

        /// <summary>上限（设置页根据 CPU 物理核数动态收紧）</summary>
        public decimal Maximum
        {
            get => _box.Maximum;
            set => _box.Maximum = value;
        }

        public int MeasureHeight(int width)
        {
            int textW = Math.Max(Theme.S(40), width - Theme.S(24) - Theme.S(96) - Theme.S(16));
            int h = UiToolkit.MeasureTextHeight(_title, Theme.FontBody, textW);
            if (!string.IsNullOrEmpty(_hint))
                h += Theme.S(2) + UiToolkit.MeasureParagraph(_hint, Theme.FontSmall, textW);
            return Math.Max(Theme.S(48), h + Theme.S(18));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Theme.S(30);
            int textW = Math.Max(Theme.S(40), Width - Theme.S(24) - Theme.S(96) - Theme.S(16));
            int titleH = UiToolkit.MeasureTextHeight(_title, Theme.FontBody, textW);
            int y = Theme.S(10) + Math.Max(0, (titleH - h) / 2);
            _box.SetBounds(Width - Theme.S(12) - Theme.S(96), y, Theme.S(96), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiToolkit.DrawTitleAndHint(e.Graphics, _title, _hint, Theme.S(12), Theme.S(10),
                Math.Max(Theme.S(40), Width - Theme.S(24) - Theme.S(96) - Theme.S(16)), out _);
        }
    }

    /// <summary>深色数字框（自绘，替代原生 NumericUpDown 的白色微调按钮）</summary>
    internal sealed class NumberBox : Control
    {
        private readonly decimal _min;
        private decimal _max;
        private readonly decimal _step;
        private readonly int _decimals;
        private readonly TextBox _editor;
        private decimal _value;
        private bool _hover;
        private bool _focused;

        public event EventHandler ValueChanged;

        public NumberBox(decimal min, decimal max, decimal value, int decimals, decimal step)
        {
            _min = min;
            _max = max;
            _decimals = Math.Max(0, decimals);
            _step = step <= 0 ? 1m : step;
            _value = Math.Clamp(value, min, max);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;
            _editor = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.TextPrimary,
                Font = Theme.FontBody,
                TextAlign = HorizontalAlignment.Center
            };
            _editor.KeyDown += EditorKeyDown;
            _editor.LostFocus += (s, e) => Commit();
            Controls.Add(_editor);
        }

        public decimal Value
        {
            get => _value;
            set
            {
                decimal v = Math.Clamp(value, _min, _max);
                if (_decimals == 0) v = Math.Round(v);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>上限可随机器物理核数动态调整（设置页用）</summary>
        public decimal Maximum
        {
            get => _max;
            set
            {
                if (value <= _min) return;
                _max = value;
                if (_value > _max) Value = _max;
            }
        }

        private string FormatValue() =>
            _decimals > 0
                ? _value.ToString("F" + _decimals, CultureInfo.CurrentCulture)
                : ((int)_value).ToString(CultureInfo.CurrentCulture);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = Theme.RoundedRectangle(rect, Theme.S(6)))
            {
                using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);
                Color border = _focused ? Theme.Accent : _hover ? Theme.BorderStrong : Theme.Border;
                using var pen = new Pen(border, Theme.Sf(1f));
                g.DrawPath(pen, path);
            }
            int btnW = Theme.S(20);
            TextRenderer.DrawText(g, FormatValue(), Theme.FontBodyStrong,
                new Rectangle(Theme.S(8), 0, Math.Max(1, Width - btnW - Theme.S(6)), Height),
                Theme.TextPrimary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int cx = Width - Theme.S(11);
            int cy = Height / 2;
            int a = Theme.S(4);
            using var ink = new Pen(_hover ? Theme.TextSecondary : Theme.TextMuted, Theme.Sf(1.6f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLines(ink, new[]
            {
                new Point(cx - a, cy - Theme.S(4)),
                new Point(cx, cy - Theme.S(8)),
                new Point(cx + a, cy - Theme.S(4))
            });
            g.DrawLines(ink, new[]
            {
                new Point(cx - a, cy + Theme.S(4)),
                new Point(cx, cy + Theme.S(8)),
                new Point(cx + a, cy + Theme.S(4))
            });
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.X >= Width - Theme.S(22))
            {
                Step(e.Y < Height / 2 ? 1 : -1);
                return;
            }
            BeginEdit();
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 只有聚焦时才响应滚轮，否则交给页面滚动（避免滑页面时误改数值）
            if (Focused || _editor.Focused)
            {
                Step(Math.Sign(e.Delta));
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { BeginEdit(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        private void Step(int direction)
        {
            Value = _value + direction * _step;
        }

        private void BeginEdit()
        {
            if (_editor.Visible) return;
            _editor.Text = FormatValue();
            _editor.SetBounds(Theme.S(4), Math.Max(0, (Height - _editor.PreferredHeight) / 2),
                Math.Max(Theme.S(20), Width - Theme.S(28)), _editor.PreferredHeight);
            _editor.Visible = true;
            _focused = true;
            _editor.Focus();
            _editor.SelectAll();
            Invalidate();
        }

        private void Commit()
        {
            if (!_editor.Visible) return;
            _editor.Visible = false;
            _focused = false;
            string text = _editor.Text.Trim();
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal parsed))
                Value = parsed;
            Invalidate();
        }

        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                Commit();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _editor.Visible = false;
                _focused = false;
                Invalidate();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                Step(e.KeyCode == Keys.Up ? 1 : -1);
                _editor.Text = FormatValue();
                _editor.SelectAll();
                e.SuppressKeyPress = true;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  行 5：文本输入行
    // ══════════════════════════════════════════════════════════
    public sealed class TextRow : Panel, IRow
    {
        private readonly string _title;
        private readonly string _hint;
        private readonly TextBox _edit;

        public TextRow(string title, string hint, string value, Action<string> changed = null)
        {
            _title = title ?? "";
            _hint = hint;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            SetStyle(ControlStyles.ResizeRedraw, true);
            _edit = new TextBox
            {
                Text = value ?? "",
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.TextPrimary,
                Font = Theme.FontBody
            };
            _edit.TextChanged += (s, e) => changed?.Invoke(_edit.Text);
            Controls.Add(_edit);
        }

        public string Value
        {
            get => _edit.Text;
            set => _edit.Text = value ?? "";
        }

        public int MeasureHeight(int width)
        {
            int inner = Math.Max(Theme.S(40), width - Theme.S(24));
            int h = UiToolkit.MeasureTextHeight(_title, Theme.FontBody, inner) + Theme.S(4) + Theme.S(26);
            if (!string.IsNullOrEmpty(_hint))
                h += Theme.S(2) + UiToolkit.MeasureParagraph(_hint, Theme.FontSmall, inner);
            return h + Theme.S(16);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int titleH = UiToolkit.MeasureTextHeight(_title, Theme.FontBody, Math.Max(Theme.S(40), Width - Theme.S(24)));
            _edit.SetBounds(Theme.S(12) + Theme.S(8), Theme.S(8) + titleH + Theme.S(4),
                Math.Max(Theme.S(40), Width - Theme.S(24) - Theme.S(16)), _edit.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (string.IsNullOrEmpty(_title)) return;
            TextRenderer.DrawText(g, _title, Theme.FontBody,
                new Rectangle(Theme.S(12), Theme.S(8), Math.Max(1, Width - Theme.S(24)), Theme.FontBody.Height),
                Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            // 输入框圆角底板（TextBox 无边框，靠底板提供视觉边界）
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int titleH = UiToolkit.MeasureTextHeight(_title, Theme.FontBody, Math.Max(Theme.S(40), Width - Theme.S(24)));
            var box = new Rectangle(Theme.S(12), Theme.S(8) + titleH + Theme.S(4),
                Math.Max(1, Width - Theme.S(24)), Theme.S(26));
            using var path = Theme.RoundedRectangle(box, Theme.S(6));
            using var fill = new SolidBrush(Theme.Surface);
            using var pen = new Pen(Theme.BorderStrong, Theme.Sf(1f));
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  行 6：按钮行
    // ══════════════════════════════════════════════════════════
    public sealed class ButtonRow : Panel, IRow
    {
        private readonly List<Control> _buttons = new();
        private string _note;

        public ButtonRow()
        {
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            Height = Theme.S(52);
        }

        public ButtonRow Add(Control button)
        {
            _buttons.Add(button);
            Controls.Add(button);
            return this;
        }

        public ButtonRow WithNote(string note)
        {
            _note = note;
            Invalidate();
            return this;
        }

        public int MeasureHeight(int width) => Theme.S(52);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Theme.S(12);
            foreach (var b in _buttons)
            {
                b.SetBounds(x, (Height - b.Height) / 2, b.Width, b.Height);
                x += b.Width + Theme.S(8);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(_note)) return;
            TextRenderer.DrawText(e.Graphics, _note, Theme.FontSmall,
                new Rectangle(Theme.S(12), 0, Math.Max(1, Width - Theme.S(24)), Height),
                Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  卡片
    // ══════════════════════════════════════════════════════════
    public sealed class Card : Panel
    {
        private readonly string _title;
        private readonly string _hint;
        private readonly List<Control> _rows = new();
        private Control _fill;

        public Card(string title = null, string hint = null)
        {
            _title = title;
            _hint = hint;
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }


        public int HeaderHeight =>
            string.IsNullOrEmpty(_title) ? Theme.S(14)
            : Theme.S(50) + (string.IsNullOrEmpty(_hint) ? 0 : Theme.S(18));

        public Card Add(Control row)
        {
            if (row == null) return this;
            _rows.Add(row);
            Controls.Add(row);
            return this;
        }

        public Card AddFill(Control control)
        {
            _fill = control;
            Controls.Add(control);
            return this;
        }

        public int MeasureHeight(int width)
        {
            int inner = Math.Max(Theme.S(80), width - Theme.S(24));
            int h = HeaderHeight;
            foreach (var row in _rows)
                h += row is IRow r ? r.MeasureHeight(inner) : row.Height;
            h += Theme.S(10);
            return h;
        }

        private Region _region;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using var path = Theme.RoundedRectangle(new Rectangle(0, 0, Width, Height), Theme.S(12));
            var next = new Region(path);
            var previous = Region;
            Region = next;
            previous?.Dispose();
            _region = next;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int inner = Math.Max(Theme.S(80), ClientSize.Width - Theme.S(24));
            int y = HeaderHeight;
            foreach (var row in _rows)
            {
                int h = row is IRow r ? r.MeasureHeight(inner) : row.Height;
                row.SetBounds(Theme.S(12), y, inner, h);
                y += h;
            }
            if (_fill != null)
            {
                int h = Math.Max(Theme.S(60), ClientSize.Height - HeaderHeight - Theme.S(10));
                _fill.SetBounds(Theme.S(12), HeaderHeight, inner, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedRectangle(
                       new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Theme.S(12)))
            using (var pen = new Pen(Theme.Border, Theme.Sf(1f)))
                g.DrawPath(pen, path);
            if (string.IsNullOrEmpty(_title)) return;
            TextRenderer.DrawText(g, _title, Theme.FontCardTitle,
                new Rectangle(Theme.S(18), Theme.S(16), Math.Max(1, Width - Theme.S(36)), Theme.S(24)),
                Theme.TextPrimary, TextFormatFlags.Left | UiToolkit.TextBoxFlags());
            if (!string.IsNullOrEmpty(_hint))
                TextRenderer.DrawText(g, _hint, Theme.FontSmall,
                    new Rectangle(Theme.S(18), Theme.S(40), Math.Max(1, Width - Theme.S(36)), Theme.S(16)),
                    Theme.TextMuted, TextFormatFlags.Left | UiToolkit.TextBoxFlags());
        }
    }

    // ══════════════════════════════════════════════════════════
    //  栅格（列数固定，行高自动对齐）
    // ══════════════════════════════════════════════════════════
    public sealed class CardGrid : Panel
    {
        private readonly List<Card> _cards = new();
        private readonly HashSet<Card> _wide = new();

        public CardGrid(int columns)
        {
            Columns = Math.Max(1, columns);
            DoubleBuffered = true;
            BackColor = Theme.Bg;
        }

        public int Columns { get; }

        /// <summary>内容高度随卡片变化（滚动容器内使用）</summary>
        public bool AutoHeight { get; set; } = true;

        /// <summary>高度富余时把多余空间均分到各行（填满容器用）</summary>
        public bool Stretch { get; set; }

        public int ContentHeight { get; private set; }

        /// <summary>按给定宽度估算整片栅格高度（供页面分配空间，避免卡片被裁切）</summary>
        public int PreferredHeight(int width)
        {
            if (_cards.Count == 0) return 0;
            int gapX = Theme.S(16), gapY = Theme.S(16);
            int columnWidth = (width - gapX * (Columns - 1)) / Columns;
            if (columnWidth <= 0) return 0;
            int total = 0;
            int i = 0;
            while (i < _cards.Count)
            {
                if (_wide.Contains(_cards[i]))
                {
                    total += _cards[i].MeasureHeight(width);
                    i++;
                }
                else
                {
                    int taken = 0, height = 0;
                    while (i + taken < _cards.Count && taken < Columns && !_wide.Contains(_cards[i + taken]))
                    {
                        height = Math.Max(height, _cards[i + taken].MeasureHeight(columnWidth));
                        taken++;
                    }
                    total += height;
                    i += taken;
                }
                if (i < _cards.Count) total += gapY;
            }
            return total;
        }

        public Card Add(Card card)
        {
            _cards.Add(card);
            Controls.Add(card);
            return card;
        }

        /// <summary>整行卡片（跨全部列，例如诊断信息）</summary>
        public Card AddWide(Card card)
        {
            _cards.Add(card);
            _wide.Add(card);
            Controls.Add(card);
            return card;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int gapX = Theme.S(16), gapY = Theme.S(16);
            int available = ClientSize.Width;
            int columnWidth = (available - gapX * (Columns - 1)) / Columns;
            if (columnWidth <= 0 || available <= 0) return;

            // 第一遍：算出每行的起始下标、卡片数与所需高度
            var rowStart = new List<int>();
            var rowCount = new List<int>();
            var rowHeight = new List<int>();
            int i = 0;
            while (i < _cards.Count)
            {
                if (_wide.Contains(_cards[i]))
                {
                    rowStart.Add(i);
                    rowCount.Add(1);
                    rowHeight.Add(_cards[i].MeasureHeight(available));
                    i++;
                    continue;
                }
                int taken = 0, height = 0;
                while (i + taken < _cards.Count && taken < Columns && !_wide.Contains(_cards[i + taken]))
                {
                    height = Math.Max(height, _cards[i + taken].MeasureHeight(columnWidth));
                    taken++;
                }
                rowStart.Add(i);
                rowCount.Add(taken);
                rowHeight.Add(height);
                i += taken;
            }

            int total = gapY * Math.Max(0, rowHeight.Count - 1);
            foreach (int h in rowHeight) total += h;

            // 第二遍：填满容器时把富余高度均分给各行（走势图等需要长高的卡片）
            if (Stretch && !AutoHeight && rowHeight.Count > 0 && ClientSize.Height > total)
            {
                int extra = (ClientSize.Height - total) / rowHeight.Count;
                for (int r = 0; r < rowHeight.Count; r++) rowHeight[r] += extra;
                total += extra * rowHeight.Count;
            }

            // 第三遍：摆放
            int y = 0;
            for (int r = 0; r < rowHeight.Count; r++)
            {
                int start = rowStart[r];
                if (rowCount[r] == 1 && _wide.Contains(_cards[start]))
                {
                    _cards[start].SetBounds(0, y, available, rowHeight[r]);
                }
                else
                {
                    for (int c = 0; c < rowCount[r]; c++)
                        _cards[start + c].SetBounds(c * (columnWidth + gapX), y, columnWidth, rowHeight[r]);
                }
                y += rowHeight[r] + gapY;
            }

            ContentHeight = Math.Max(0, y - (rowHeight.Count > 0 ? gapY : 0));
            if (AutoHeight && Height != ContentHeight) Height = ContentHeight;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  滚动容器（自绘细滚动条，替代原生白条）
    // ══════════════════════════════════════════════════════════
    public sealed class ScrollHost : Panel
    {
        private readonly Control _content;
        private int _offset;

        public ScrollHost(Control content)
        {
            _content = content;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Controls.Add(content);
            content.Resize += (s, e) => ClampOffset();
        }

        public void ScrollToTop()
        {
            _offset = 0;
            ApplyOffset();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            ClampOffset();
        }

        private int MaxOffset => Math.Max(0, _content.Height - ClientSize.Height);

        private void ClampOffset()
        {
            int max = MaxOffset;
            if (_offset > max) _offset = max;
            if (_offset < 0) _offset = 0;
            ApplyOffset();
        }

        private bool _applying;

        private void ApplyOffset()
        {
            if (_applying) return;
            _applying = true;
            try
            {
                int width = Math.Max(Theme.S(120), ClientSize.Width - Theme.S(8));
                _content.SetBounds(0, -_offset, width, _content.Height);
            }
            finally { _applying = false; }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = MaxOffset;
            if (max > 0)
            {
                _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * Theme.S(80), 0, max);
                ApplyOffset();
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int max = MaxOffset;
            if (max <= 0 || ClientSize.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int track = ClientSize.Height - Theme.S(8);
            int thumb = Math.Max(Theme.S(36),
                (int)(track * (double)ClientSize.Height / Math.Max(1, _content.Height)));
            int pos = (int)((track - thumb) * (_offset / (double)max));
            var rect = new Rectangle(Width - Theme.S(7), Theme.S(4) + pos, Theme.S(4), thumb);
            using var path = Theme.RoundedRectangle(rect, Theme.S(2));
            using var brush = new SolidBrush(Color.FromArgb(150, Theme.TextMuted));
            g.FillPath(brush, path);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  页头 / 工具条
    // ══════════════════════════════════════════════════════════
    public sealed class PageHeader : Panel
    {
        private readonly string _title;
        private readonly string _subtitle;
        private readonly List<Control> _actions = new();

        public PageHeader(string title, string subtitle)
        {
            _title = title ?? "";
            _subtitle = subtitle ?? "";
            Height = Theme.S(84);
            BackColor = Theme.Bg;
            DoubleBuffered = true;
        }

        public void AddAction(Control action)
        {
            _actions.Add(action);
            Controls.Add(action);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Width;
            foreach (var action in _actions)
            {
                x -= action.Width;
                action.SetBounds(x, Theme.S(6) + Math.Max(0, (Theme.S(30) - action.Height) / 2),
                    action.Width, action.Height);
                x -= Theme.S(10);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int titleH = TextRenderer.MeasureText(g, _title, Theme.FontDisplay, new Size(Width, 0),
                TextFormatFlags.NoPadding).Height;
            TextRenderer.DrawText(g, _title, Theme.FontDisplay,
                new Rectangle(0, Theme.S(2), Math.Max(1, Width), titleH + Theme.S(6)),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            if (string.IsNullOrEmpty(_subtitle)) return;
            TextRenderer.DrawText(g, _subtitle, Theme.FontBody,
                new Rectangle(Theme.S(1), Theme.S(2) + titleH + Theme.S(4),
                    Math.Max(1, Width - Theme.S(180)), Math.Max(1, Height - titleH - Theme.S(8))),
                Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>一行按钮/说明（左对齐按钮，右对齐说明）</summary>
    public sealed class Toolbar : Panel
    {
        private readonly List<Control> _left = new();
        private readonly List<Control> _right = new();

        public Toolbar()
        {
            Height = Theme.S(54);
            BackColor = Theme.Bg;
            DoubleBuffered = true;
        }

        public Toolbar AddLeft(Control control)
        {
            _left.Add(control);
            Controls.Add(control);
            return this;
        }

        public Toolbar AddRight(Control control)
        {
            _right.Add(control);
            Controls.Add(control);
            return this;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = 0;
            foreach (var control in _left)
            {
                control.SetBounds(x, (Height - control.Height) / 2, control.Width, control.Height);
                x += control.Width + Theme.S(8);
            }
            int rx = Width - Theme.S(2);
            for (int i = _right.Count - 1; i >= 0; i--)
            {
                var control = _right[i];
                rx -= control.Width;
                control.SetBounds(rx, (Height - control.Height) / 2, control.Width, control.Height);
                rx -= Theme.S(12);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  静态工厂
    // ══════════════════════════════════════════════════════════
    public static class UiToolkit
    {
        public static int SwitchWidth => Theme.S(44);
        public static int SwitchHeight => Theme.S(24);

        private static readonly TextFormatFlags BoxFlags =
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public static TextFormatFlags TextBoxFlags() => BoxFlags;

        /// <summary>移除 emoji / 符号字形（Segoe UI 下会显示成方框）</summary>
        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsSurrogate(c)) continue;
                var category = char.GetUnicodeCategory(c);
                if (category == UnicodeCategory.OtherSymbol || category == UnicodeCategory.ModifierSymbol) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static int MeasureTextHeight(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return font.Height;
            return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        /// <summary>多行文本高度（TextRenderer.MeasureText 不统计换行，必须逐行累加）</summary>
        public static int MeasureParagraph(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int total = 0;
            foreach (string line in text.Split('\n'))
                total += MeasureTextHeight(line.TrimEnd('\r'), font, width);
            return total;
        }

        private static int MeasureTextWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        /// <summary>行内“标题 + 灰色说明”的统一绘制（返回占用高度）</summary>
        internal static int DrawTitleAndHint(Graphics g, string title, string hint,
            int x, int y, int width, out int titleHeight)
        {
            titleHeight = MeasureTextHeight(title, Theme.FontBodyStrong, width);
            TextRenderer.DrawText(g, title, Theme.FontBodyStrong,
                new Rectangle(x, y, Math.Max(1, width), titleHeight + Theme.S(2)),
                Theme.TextPrimary, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Top);
            int total = titleHeight;
            if (!string.IsNullOrEmpty(hint))
            {
                int hintH = MeasureTextHeight(hint, Theme.FontSmall, width);
                TextRenderer.DrawText(g, hint, Theme.FontSmall,
                    new Rectangle(x, y + titleHeight + Theme.S(2), Math.Max(1, width), hintH + Theme.S(2)),
                    Theme.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Top);
                total += Theme.S(2) + hintH;
            }
            return total;
        }

        /// <summary>药丸拨杆</summary>
        internal static void DrawSwitch(Graphics g, int x, int y, int width, int height,
            bool on, bool hot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(x, y, width, height);
            using (var path = Theme.RoundedRectangle(track, height / 2))
            {
                Color color = on ? Theme.Accent : hot ? Theme.BorderStrong : Theme.Border;
                using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            }
            int knobSize = Math.Max(Theme.S(8), height - Theme.S(6));
            int knobX = on ? track.Right - knobSize - Theme.S(3) : track.Left + Theme.S(3);
            using var knob = new SolidBrush(on ? Color.White : Theme.TextSecondary);
            g.FillEllipse(knob, knobX, y + Theme.S(3), knobSize, knobSize);
        }


        public static Button CreateButton(string text)
        {
            Theme.Initialize();
            var button = new FlatButton(text, false);
            button.Size = new Size(MeasureTextWidth(text, button.Font) + Theme.S(30), Theme.S(32));
            return button;
        }

        public static Button CreatePrimaryButton(string text)
        {
            Theme.Initialize();
            var button = new FlatButton(text, true);
            button.Size = new Size(MeasureTextWidth(text, button.Font) + Theme.S(34), Theme.S(32));
            return button;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  自绘表格（替代原生 ListView：没有白色滚动条，也没有系统列分隔线）
    // ══════════════════════════════════════════════════════════

    /// <summary>表格行（Key 用于刷新后保持选中）</summary>
    public sealed class DataRow
    {
        public string Key;
        public string[] Cells;

        public DataRow(string key, params string[] cells)
        {
            Key = key ?? string.Empty;
            Cells = cells ?? Array.Empty<string>();
        }

        public string Cell(int index) =>
            index >= 0 && index < Cells.Length ? Cells[index] ?? string.Empty : string.Empty;
    }

    /// <summary>深色表格：自绘表头 / 斑马纹 / 选中行 / 细滚动条</summary>
    public sealed class DataList : Panel
    {
        private readonly (string title, int width)[] _columns;
        private readonly List<DataRow> _rows = new();
        private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
        private int _scroll;
        private int _hover = -1;

        public DataList(params (string title, int width)[] columns)
        {
            _columns = columns ?? Array.Empty<(string, int)>();
            DoubleBuffered = true;
            BackColor = Theme.SurfaceAlt;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public bool MultiSelect { get; set; }

        public event EventHandler SelectionChanged;

        public event EventHandler RowActivated;

        private int RowHeight => Theme.S(32);

        private int HeaderHeight => Theme.S(32);

        public List<DataRow> SelectedRows
        {
            get
            {
                var list = new List<DataRow>();
                foreach (var row in _rows)
                    if (_selected.Contains(row.Key)) list.Add(row);
                return list;
            }
        }

        public DataRow FirstSelected
        {
            get
            {
                foreach (var row in _rows)
                    if (_selected.Contains(row.Key)) return row;
                return null;
            }
        }

        /// <summary>整表替换（按 Key 保持选中，滚动位置自动钳制）</summary>
        public void SetRows(IEnumerable<DataRow> rows)
        {
            _rows.Clear();
            if (rows != null)
                foreach (var row in rows)
                    if (row != null) _rows.Add(row);
            var alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _rows) alive.Add(row.Key);
            _selected.RemoveWhere(key => !alive.Contains(key));
            int max = MaxScroll;
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
            if (_hover >= _rows.Count) _hover = -1;
            Invalidate();
        }


        private int MaxScroll =>
            Math.Max(0, _rows.Count * RowHeight - Math.Max(0, ClientSize.Height - HeaderHeight));

        private int[] ColumnWidths(int totalWidth)
        {
            var widths = new int[_columns.Length];
            int used = 0;
            for (int i = 0; i < _columns.Length; i++)
            {
                widths[i] = Theme.S(_columns[i].width);
                used += widths[i];
            }
            if (widths.Length > 0)
            {
                int last = widths.Length - 1;
                widths[last] = Math.Max(Theme.S(60), totalWidth - (used - widths[last]));
            }
            return widths;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            int header = HeaderHeight;
            var widths = ColumnWidths(w);

            using (var brush = new SolidBrush(Theme.Surface)) g.FillRectangle(brush, 0, 0, w, header);
            int x = 0;
            for (int i = 0; i < _columns.Length; i++)
            {
                var cell = new Rectangle(x + Theme.S(12), 0, Math.Max(1, widths[i] - Theme.S(24)), header);
                TextRenderer.DrawText(g, _columns[i].title, Theme.FontBodyStrong, cell, Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                x += widths[i];
            }
            using (var pen = new Pen(Theme.Border, Theme.Sf(1f)))
                g.DrawLine(pen, 0, header - 1, w, header - 1);

            int bodyTop = header;
            int bodyHeight = Math.Max(0, h - header);
            using (var brush = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(brush, 0, bodyTop, w, bodyHeight);

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(0, bodyTop, w, bodyHeight));
            if (_rows.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无数据", Theme.FontSmall,
                    new Rectangle(0, bodyTop, w, Math.Max(1, bodyHeight)), Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                int rh = RowHeight;
                int first = _scroll / rh;
                int offset = _scroll % rh;
                int visible = bodyHeight / rh + 2;
                for (int i = 0; i < visible; i++)
                {
                    int index = first + i;
                    if (index >= _rows.Count) break;
                    int y = bodyTop + i * rh - offset;
                    if (y + rh <= bodyTop) continue;
                    bool selected = _selected.Contains(_rows[index].Key);
                    Color back = selected ? Theme.AccentSoft
                        : index == _hover ? Theme.SurfaceHover
                        : index % 2 == 1 ? Color.FromArgb(19, 23, 30) : Theme.SurfaceAlt;
                    using (var brush = new SolidBrush(back)) g.FillRectangle(brush, 0, y, w, rh);
                    int cx = 0;
                    for (int c = 0; c < _columns.Length; c++)
                    {
                        var cell = new Rectangle(cx + Theme.S(12), y, Math.Max(1, widths[c] - Theme.S(24)), rh);
                        TextRenderer.DrawText(g, _rows[index].Cell(c),
                            c == 0 ? Theme.FontBodyStrong : Theme.FontBody, cell,
                            selected || c == 0 ? Theme.TextPrimary : Theme.TextSecondary,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                        cx += widths[c];
                    }
                }
            }
            g.Clip = oldClip;

            int max = MaxScroll;
            if (max <= 0 || bodyHeight <= 0) return;
            int track = bodyHeight - Theme.S(8);
            int thumb = Math.Max(Theme.S(32),
                (int)(track * (double)bodyHeight / Math.Max(1, _rows.Count * RowHeight)));
            int pos = (int)((track - thumb) * (_scroll / (double)max));
            var bar = new Rectangle(w - Theme.S(7), bodyTop + Theme.S(4) + pos, Theme.S(4), thumb);
            using (var path = Theme.RoundedRectangle(bar, Theme.S(2)))
            using (var brush = new SolidBrush(Color.FromArgb(150, Theme.TextMuted)))
                g.FillPath(brush, path);
        }

        private int RowAt(int y)
        {
            if (y < HeaderHeight) return -1;
            int index = (_scroll + y - HeaderHeight) / RowHeight;
            return index >= 0 && index < _rows.Count ? index : -1;
        }

        private void EnsureVisible(int index)
        {
            if (index < 0) return;
            int rh = RowHeight;
            int view = Math.Max(1, ClientSize.Height - HeaderHeight);
            int top = index * rh;
            if (top < _scroll) _scroll = top;
            else if (top + rh > _scroll + view) _scroll = top + rh - view;
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int index = RowAt(e.Y);
            if (index >= 0)
            {
                var row = _rows[index];
                bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
                if (MultiSelect && ctrl)
                {
                    if (!_selected.Remove(row.Key)) _selected.Add(row.Key);
                }
                else
                {
                    _selected.Clear();
                    _selected.Add(row.Key);
                }
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (RowAt(e.Y) >= 0) RowActivated?.Invoke(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = RowAt(e.Y);
            if (index != _hover)
            {
                _hover = index;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1)
            {
                _hover = -1;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = MaxScroll;
            if (max > 0)
            {
                _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * RowHeight * 3, 0, max);
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Up || keyData == Keys.Down ||
            keyData == Keys.PageUp || keyData == Keys.PageDown || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int current = -1;
            var selected = FirstSelected;
            if (selected != null) current = _rows.IndexOf(selected);
            int page = Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight - 1);
            int next = current;
            switch (e.KeyCode)
            {
                case Keys.Up: next = Math.Max(0, current < 0 ? 0 : current - 1); break;
                case Keys.Down: next = Math.Min(_rows.Count - 1, current + 1); break;
                case Keys.PageUp: next = Math.Max(0, current - page); break;
                case Keys.PageDown: next = Math.Min(_rows.Count - 1, current + page); break;
                default: base.OnKeyDown(e); return;
            }
            if (next >= 0 && next < _rows.Count && next != current)
            {
                _selected.Clear();
                _selected.Add(_rows[next].Key);
                EnsureVisible(next);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            base.OnKeyDown(e);
        }
    }

    /// <summary>扁平按钮（自绘圆角，主按钮用强调色）</summary>
    internal sealed class FlatButton : Button
    {
        private readonly bool _primary;
        private bool _hover;
        private bool _down;

        public FlatButton(string text, bool primary)
        {
            _primary = primary;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = primary ? Theme.Accent : Theme.Surface;
            ForeColor = primary ? Theme.AccentInk : Theme.TextPrimary;
            Font = Theme.FontBodyStrong;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color fill = _primary
                ? (_down ? Theme.Accent : _hover ? Color.FromArgb(140, 190, 255) : Theme.Accent)
                : (_down ? Theme.SurfaceHover : _hover ? Theme.SurfaceHover : Theme.Surface);
            using (var path = Theme.RoundedRectangle(rect, Theme.S(8)))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (!_primary)
                    using (var pen = new Pen(_hover ? Theme.BorderStrong : Theme.Border, Theme.Sf(1f)))
                        g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect,
                _primary ? Theme.AccentInk : Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>把控件滚动条切到系统暗色皮肤（不支持的版本自动忽略）</summary>
    internal static class DarkScrollbars
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        public static void Apply(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { SetWindowTheme(handle, "DarkMode_Explorer", null); } catch { }
        }
    }
}
