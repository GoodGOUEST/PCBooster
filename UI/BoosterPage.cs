// ============================================================
// PC Booster v3.6.8
// UI/BoosterPage.cs
// 页面基类（UI 重写）: 统一页头 + 内容区 + 定时器生命周期
// 说明: 页面不再依赖 AutoScroll（原生白滚动条会破相），需要滚动的页面
//       自己在内容区放 ScrollHost。
// ============================================================
using System.Drawing;
using System.Windows.Forms;

namespace PCBooster.UI
{
    /// <summary>页面基类：深色底 + 页头 + 内容区 + 隐藏即停表</summary>
    public class BoosterPage : Panel
    {
        private readonly System.Windows.Forms.Timer _timer;

        /// <summary>页头（可为 null，例如嵌入型页面）</summary>
        protected PageHeader Header { get; }

        /// <summary>内容区（页头以下的全部空间）</summary>
        protected Panel Content { get; }

        protected BoosterPage(int refreshMs = 0, string title = null, string subtitle = null)
        {
            Theme.Initialize();
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            AutoScroll = false;
            Content = new Panel { BackColor = Theme.Bg };
            Controls.Add(Content);
            if (!string.IsNullOrEmpty(title))
            {
                Header = new PageHeader(title, subtitle);
                Controls.Add(Header);
            }
            if (refreshMs > 0)
            {
                _timer = new System.Windows.Forms.Timer { Interval = refreshMs };
                _timer.Tick += (s, e) => OnRefresh();
            }
        }

        /// <summary>内容区排布（子类手动放置，避免停靠顺序歧义）</summary>
        protected virtual void LayoutContent(Size size) { }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Content == null) return;   // 构造期间 AutoScroll 赋值会先触发一次布局
            int headerHeight = Header == null ? 0 : Header.Height;
            if (Header != null) Header.SetBounds(0, 0, ClientSize.Width, headerHeight);
            Content.SetBounds(0, headerHeight, ClientSize.Width,
                System.Math.Max(0, ClientSize.Height - headerHeight));
            // 基类构造期间也会触发一次布局，此时子类字段尚未初始化，跳过
            if (Parent != null) LayoutContent(Content.ClientSize);
        }

        public virtual void OnPageShown() => _timer?.Start();

        protected virtual void OnRefresh() { }

        /// <summary>页面切走时显式停表（父面板 Clear 不触发 VisibleChanged）</summary>
        public virtual void OnPageHidden() => _timer?.Stop();

        protected override void OnVisibleChanged(System.EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) _timer?.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
