// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/AIPage.cs
// 智能预测页（UI 重写）：预测 / 统计 / 分类 + 原理说明
// ============================================================
using System.Drawing;
using System.Windows.Forms;
using PCBooster.Infrastructure.Common;
using PCBooster.Intelligence;

namespace PCBooster.UI
{
    /// <summary>智能预测页面</summary>
    public class AIPage : BoosterPage
    {
        private readonly CardGrid _cards;
        private readonly Card _help;
        private readonly TextBlock _prediction;
        private readonly TextBlock _stats;
        private readonly TextBlock _classes;

        public AIPage() : base(3000, "智能预测", "学习你的使用习惯，只提前预热已运行的程序；可随时关闭。")
        {
            _cards = new CardGrid(3) { AutoHeight = false, Stretch = true };
            _prediction = new TextBlock("加载中…", Theme.FontBody, Theme.TextPrimary);
            _stats = new TextBlock("—", Theme.FontBody, Theme.TextSecondary);
            _classes = new TextBlock("—", Theme.FontBody, Theme.TextSecondary);
            _cards.Add(new Card("接下来可能使用")).Add(_prediction);
            _cards.Add(new Card("引擎统计")).Add(_stats);
            _cards.Add(new Card("分类分布")).Add(_classes);

            _help = new Card("它是怎么工作的");
            _help.Add(new TextBlock(
                "记录    启动时间、切到前台的次数、时段与星期规律、前后程序顺序（开启采集后才有）\n" +
                "预测    9 个维度加权打分（时段 / 星期 / 前台 / 固定规律 / 序列 / 场景 / 命中率 / 探索）\n" +
                "预加载  只对“已经在运行”的程序预热工作集，绝不会自行启动程序\n" +
                "反馈    命中加分、落空减分；预加载阈值按最近 40 次结果自动升降\n\n" +
                "隐私    默认关闭；数据仅保存在本机。关闭采集后不再记录，但场景识别仍在内存中工作，\n" +
                "        只是预测与置信度停止更新。",
                Theme.FontBody, Theme.TextSecondary));

            Content.Controls.Add(_cards);
            Content.Controls.Add(_help);
        }

        protected override void LayoutContent(Size size)
        {
            int top = _cards.PreferredHeight(size.Width);
            _cards.SetBounds(0, 0, size.Width, top);
            int helpTop = top + Theme.S(12);
            _help.SetBounds(0, helpTop, size.Width, System.Math.Max(Theme.S(140), size.Height - helpTop));
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            OnRefresh();
        }

        protected override void OnRefresh()
        {
            if (!AppConfig.EnableDataCollection)
            {
                _prediction.Text = "数据采集已关闭\n\n开启后才会记录使用习惯；\n当前仍可识别场景，但不会产生预测。";
                _prediction.ForeColor = Theme.TextSecondary;
                _stats.Text = UiToolkit.Plain(IntelligenceEngine.GetStats());
                _classes.Text = "—";
                LayoutContent(Content.ClientSize);
                return;
            }

            _prediction.ForeColor = Theme.TextPrimary;
            var predictions = IntelligenceEngine.Predict(5);
            if (predictions.Count == 0)
            {
                int minutes = IntelligenceEngine.GetCollectedMinutes();
                _prediction.Text = minutes < Constants.COLD_START_MINUTES
                    ? $"学习中\n\n已采集 {minutes}/{Constants.COLD_START_MINUTES} 分钟\n保持正常使用即可，学满后开始预热。"
                    : "暂无预测\n\n学习数据仍在积累，规律稳定后会自动出现。";
            }
            else
            {
                var lines = new List<string>();
                foreach (var p in predictions)
                    lines.Add($"{p.process}\n    置信度 {p.confidence:P0}");
                _prediction.Text = string.Join("\n", lines);
            }

            _stats.Text = string.Join("\n", UiToolkit.Plain(IntelligenceEngine.GetStats())
                .Split(new[] { ',', '，', '|', '·' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
            var summary = IntelligenceEngine.GetClassificationSummary();
            _classes.Text = summary.Count > 0
                ? string.Join("\n", summary.Select(kv => $"{IntelligenceEngine.GetClassName(kv.Key)}    {kv.Value} 个"))
                : "暂无分类数据";
            LayoutContent(Content.ClientSize);
        }
    }
}
