// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// UI/DecisionPage.cs
// 决策日志页（UI 重写）：事件入队 + 250ms 批量刷 UI + 订阅释放
// ============================================================
using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using PCBooster.Core;
using PCBooster.Infrastructure.Common;

namespace PCBooster.UI
{
    /// <summary>决策日志页面</summary>
    public class DecisionPage : BoosterPage
    {
        private readonly RichTextBox _log;
        private readonly Card _card;
        private readonly Toolbar _bar;
        private readonly Label _necessity;
        private readonly System.Windows.Forms.Timer _flushTimer;
        private readonly ConcurrentQueue<(string message, Color color)> _pending = new();
        private const int PendingMax = 500;

        private readonly Action<ProtectionExpiredEvent> _onProtectionExpired;
        private readonly Action<PredictionResultEvent> _onPredictionResult;
        private readonly Action<SceneChangedEvent> _onSceneChanged;
        private readonly Action<LoadLevelChangedEvent> _onLoadLevelChanged;
        private readonly Action<ProcessLaunchedEvent> _onProcessLaunched;
        private readonly Action<ProcessExitedEvent> _onProcessExited;
        private readonly Action<PreloadCompletedEvent> _onPreloadCompleted;
        private readonly Action<RuleAppliedEvent> _onRuleApplied;
        private readonly Action<ModuleHealthChangedEvent> _onModuleHealth;
        private readonly Action<PowerStateChangedEvent> _onPowerState;
        private readonly Action<SystemWakeFromSleepEvent> _onWake;
        private readonly Action<IdleLearningCompletedEvent> _onIdleLearning;

        public DecisionPage() : base(2000, "日志", "每一次判断、预加载与场景切换都在这里留痕。")
        {
            _necessity = new Label
            {
                Text = DecisionNecessityEvaluator.GetScoreReport(),
                AutoSize = false,
                Width = Theme.S(300),
                Height = Theme.S(20),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                Font = Theme.FontSmall,
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Bg
            };
            _bar = new Toolbar();
            _bar.AddRight(_necessity);

            _card = new Card("最近事件", "最多保留 500 行");
            _log = new RichTextBox
            {
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.TextPrimary,
                Font = Theme.FontMono,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };
            _log.HandleCreated += (s, e) => DarkScrollbars.Apply(_log.Handle);
            _card.AddFill(_log);

            Content.Controls.Add(_bar);
            Content.Controls.Add(_card);

            _onProtectionExpired = evt => Append($"保护过期: {evt.ProcessName} ({evt.Reason})", Theme.Warning);
            _onPredictionResult = evt => Append($"{(evt.Hit ? "命中" : "落空")}: {evt.ProcessName} ({evt.Confidence:P0})",
                evt.Hit ? Theme.Success : Theme.Danger);
            _onSceneChanged = evt => Append($"场景: {evt.OldScene} → {evt.NewScene}", Theme.Accent);
            _onLoadLevelChanged = evt => Append($"负载: {evt.OldLevel} → {evt.NewLevel}", Theme.Warning);
            _onProcessLaunched = evt => Append($"启动: {evt.ProcessName} (PID {evt.Pid})", Theme.TextSecondary);
            _onProcessExited = evt => Append($"退出: {evt.ProcessName}", Theme.TextSecondary);
            _onPreloadCompleted = evt => Append($"预加载: {evt.ProcessName} {(evt.Success ? "成功" : "失败")}",
                evt.Success ? Theme.Success : Theme.Danger);
            _onRuleApplied = evt => Append($"规则: {evt.RuleName} → {evt.ProcessName}", Theme.Accent);
            _onModuleHealth = evt => Append($"模块 {evt.ModuleName}: {evt.Status} (连续失败 {evt.ConsecutiveFailures})",
                evt.Status == "Healthy" ? Theme.TextSecondary : Theme.Warning);
            _onPowerState = evt => Append($"电源: {evt.OldState} → {evt.NewState}", Theme.Warning);
            _onWake = evt => Append($"休眠唤醒 (间隔 {evt.GapMs}ms)", Theme.Warning);
            _onIdleLearning = evt => Append($"闲时学习: {evt.DurationMs}ms, 固化 {evt.PatternsMined} 条", Theme.Success);

            EventBus.Subscribe(_onProtectionExpired);
            EventBus.Subscribe(_onPredictionResult);
            EventBus.Subscribe(_onSceneChanged);
            EventBus.Subscribe(_onLoadLevelChanged);
            EventBus.Subscribe(_onProcessLaunched);
            EventBus.Subscribe(_onProcessExited);
            EventBus.Subscribe(_onPreloadCompleted);
            EventBus.Subscribe(_onRuleApplied);
            EventBus.Subscribe(_onModuleHealth);
            EventBus.Subscribe(_onPowerState);
            EventBus.Subscribe(_onWake);
            EventBus.Subscribe(_onIdleLearning);

            _flushTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _flushTimer.Tick += (s, e) => FlushPendingLogs();
        }

        protected override void LayoutContent(Size size)
        {
            _bar.SetBounds(0, 0, size.Width, _bar.Height);
            int top = _bar.Bottom;
            _card.SetBounds(0, top, size.Width, Math.Max(Theme.S(120), size.Height - top));
        }

        public override void OnPageShown()
        {
            base.OnPageShown();
            FlushPendingLogs();
            _flushTimer.Start();
        }

        public override void OnPageHidden()
        {
            base.OnPageHidden();
            _flushTimer.Stop();
        }

        protected override void OnRefresh()
        {
            string report = DecisionNecessityEvaluator.GetScoreReport();
            if (_necessity.Text != report)
            {
                _necessity.Text = report;
                _bar.PerformLayout();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                EventBus.Unsubscribe(_onProtectionExpired);
                EventBus.Unsubscribe(_onPredictionResult);
                EventBus.Unsubscribe(_onSceneChanged);
                EventBus.Unsubscribe(_onLoadLevelChanged);
                EventBus.Unsubscribe(_onProcessLaunched);
                EventBus.Unsubscribe(_onProcessExited);
                EventBus.Unsubscribe(_onPreloadCompleted);
                EventBus.Unsubscribe(_onRuleApplied);
                EventBus.Unsubscribe(_onModuleHealth);
                EventBus.Unsubscribe(_onPowerState);
                EventBus.Unsubscribe(_onWake);
                EventBus.Unsubscribe(_onIdleLearning);
                _flushTimer?.Stop();
                _flushTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>事件线程只入队（UI 线程每 250ms 批量消费）</summary>
        private void Append(string message, Color color)
        {
            if (IsDisposed) return;
            if (_pending.Count < PendingMax) _pending.Enqueue((message, color));
        }

        private void FlushPendingLogs()
        {
            if (IsDisposed || !IsHandleCreated) return;
            int budget = 200;
            while (budget-- > 0 && _pending.TryDequeue(out var entry))
                AppendToBox(entry.message, entry.color);
        }

        private void AppendToBox(string message, Color color)
        {
            _log.SelectionColor = color;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            if (_log.Lines.Length > Constants.UI_LOG_MAX_LINES)
            {
                _log.SelectionStart = 0;
                _log.SelectionLength = _log.GetFirstCharIndexFromLine(
                    _log.Lines.Length - Constants.UI_LOG_MAX_LINES);
                _log.SelectedText = "";
            }
            _log.ScrollToCaret();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FlushPendingLogs();
        }
    }
}
