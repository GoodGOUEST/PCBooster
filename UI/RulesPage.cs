// ============================================================
// PC Booster v3.6.8
// UI/RulesPage.cs
// 规则页（UI 重写）：进程规则的新建 / 编辑 / 删除 / 导入 / 导出
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using PCBooster.Execution.Rules;

namespace PCBooster.UI
{
    /// <summary>规则页</summary>
    public class RulesPage : BoosterPage
    {
        private readonly DataList _list;
        private readonly Card _card;
        private readonly Toolbar _bar;
        private readonly Label _stats;

        public RulesPage() : base(0, "规则", "为指定进程设置优先级、亲和性或预加载；双击可编辑。")
        {
            var add = UiToolkit.CreatePrimaryButton("新建");
            add.Click += (s, e) => AddRule();
            var edit = UiToolkit.CreateButton("编辑");
            edit.Click += (s, e) => EditRule();
            var remove = UiToolkit.CreateButton("删除");
            remove.Click += (s, e) => DeleteRule();
            var import = UiToolkit.CreateButton("导入");
            import.Click += (s, e) => Import();
            var export = UiToolkit.CreateButton("导出");
            export.Click += (s, e) => Export();

            _stats = new Label
            {
                Text = "—",
                AutoSize = false,
                Width = Theme.S(240),
                Height = Theme.S(20),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall,
                BackColor = Theme.Bg
            };

            _bar = new Toolbar();
            _bar.AddLeft(add).AddLeft(edit).AddLeft(remove).AddLeft(import).AddLeft(export).AddRight(_stats);

            _card = new Card("进程规则", "规则每 10 秒检查一次新进程；保护进程不会被改写");
            _list = new DataList(
                ("名称", 170), ("匹配", 170), ("动作", 150), ("来源", 80), ("命中", 70), ("状态", 70));
            _list.RowActivated += (s, e) => EditRule();
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

        public override void OnPageShown()
        {
            base.OnPageShown();
            RefreshList();
        }

        private void RefreshList()
        {
            var data = new List<DataRow>();
            foreach (var rule in ProcessRuleEngine.GetAllRules().OrderBy(r => r.Priority))
            {
                string action = rule.NormalizedAction();
                data.Add(new DataRow(
                    rule.Id,
                    rule.Name,
                    rule.ProcessPattern,
                    string.IsNullOrEmpty(action) ? "—"
                        : action == "priority" ? "优先级 → " + rule.GetPriorityName()
                        : action == "affinity" ? "亲和性"
                        : "预加载",
                    rule.Source switch
                    {
                        "user" => "用户",
                        "learn" => "学习",
                        "import" => "导入",
                        _ => "内置"
                    },
                    rule.HitCount.ToString(),
                    rule.IsEnabled ? "启用" : "停用"));
            }
            _list.SetRows(data);
            _stats.Text = UiToolkit.Plain(ProcessRuleEngine.GetStats());
            _bar.PerformLayout();
        }

        private ProcessRule SelectedRule()
        {
            var row = _list.FirstSelected;
            if (row == null || string.IsNullOrEmpty(row.Key)) return null;
            return ProcessRuleEngine.GetAllRules().FirstOrDefault(r => r.Id == row.Key);
        }

        private void AddRule()
        {
            using var form = new RuleEditForm(null);
            if (form.ShowDialog(this) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(form.Pattern))
            {
                MessageBox.Show(this, "匹配模式不能为空。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            if (form.AffinityMask < -1 || (form.Action == "affinity" && form.AffinityMask == -1))
            {
                MessageBox.Show(this,
                    "亲和性掩码无效或未填写（选 affinity 时必须填，例如 0xF 表示前 4 个逻辑核）。",
                    "无效规则", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            var rule = new ProcessRule
            {
                Name = string.IsNullOrWhiteSpace(form.RuleName) ? form.Pattern : form.RuleName,
                ProcessPattern = form.Pattern,
                Action = form.Action,
                TargetPriority = form.TargetPriority,
                TargetAffinityMask = form.AffinityMask,
                Source = "user",
                IsEnabled = true
            };
            if (rule.IsNoOp)
            {
                MessageBox.Show(this, "规则没有任何动作：动作为空时需要指定目标优先级（0~4）。",
                    "无效规则", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            ProcessRuleEngine.AddRule(rule);
            ProcessRuleEngine.Save();
            RefreshList();
        }

        private void EditRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            using var form = new RuleEditForm(rule);
            if (form.ShowDialog(this) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(form.Pattern))
            {
                MessageBox.Show(this, "匹配模式不能为空。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            if (form.AffinityMask < -1 || (form.Action == "affinity" && form.AffinityMask == -1))
            {
                MessageBox.Show(this,
                    "亲和性掩码无效或未填写（选 affinity 时必须填，例如 0xF 表示前 4 个逻辑核）。",
                    "无效规则", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            rule.Name = string.IsNullOrWhiteSpace(form.RuleName) ? rule.Name : form.RuleName;
            rule.ProcessPattern = form.Pattern;
            rule.Action = form.Action;
            rule.TargetPriority = form.TargetPriority;
            rule.TargetAffinityMask = form.AffinityMask;
            if (rule.IsNoOp)
            {
                MessageBox.Show(this, "规则没有任何动作，已取消修改。", "无效规则",
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            ProcessRuleEngine.RebuildIndex();
            ProcessRuleEngine.Save();
            RefreshList();
        }

        private void DeleteRule()
        {
            var rule = SelectedRule();
            if (rule == null)
            {
                MessageBox.Show(this, "请先在列表中选择一条规则。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "确定删除规则「" + rule.Name + "」？", "确认删除",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ProcessRuleEngine.RemoveRule(rule.Id);
            ProcessRuleEngine.Save();
            RefreshList();
        }

        private void Export()
        {
            using var dialog = new SaveFileDialog { Filter = "JSON|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string json = JsonSerializer.Serialize(ProcessRuleEngine.GetAllRules(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
        }

        private void Import()
        {
            using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var rules = JsonSerializer.Deserialize<List<ProcessRule>>(File.ReadAllText(dialog.FileName));
                int count = ProcessRuleEngine.ImportRules(rules, "import");
                ProcessRuleEngine.Save();
                MessageBox.Show(this, $"导入 {count} 条规则", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导入失败: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ══════════════════════════════════════════════════════
        /// <summary>新建 / 编辑规则对话框</summary>
        private sealed class RuleEditForm : Form
        {
            private readonly TextBox _name;
            private readonly TextBox _pattern;
            private readonly ComboBox _action;
            private readonly NumberBox _priority;
            private readonly TextBox _mask;

            public RuleEditForm(ProcessRule existing)
            {
                Theme.Initialize();
                Text = existing == null ? "新建规则" : "编辑规则";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.None;
                ClientSize = new Size(Theme.S(480), Theme.S(330));
                BackColor = Theme.Bg;
                ForeColor = Theme.TextPrimary;
                Font = Theme.FontBody;

                _name = NewText(existing?.Name ?? "");
                _pattern = NewText(existing?.ProcessPattern ?? "");
                _action = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Theme.Surface,
                    ForeColor = Theme.TextPrimary,
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontBody
                };
                _action.Items.AddRange(new object[] { "不指定动作", "priority（改优先级）", "affinity（绑核心）", "preload（预加载）" });
                _priority = new NumberBox(-1m, 4m, existing == null ? -1m : Math.Clamp(existing.TargetPriority, -1, 4), 0, 1m);
                _mask = NewText(existing != null && existing.TargetAffinityMask >= 0
                    ? "0x" + existing.TargetAffinityMask.ToString("X")
                    : "");

                int y = Theme.S(20);
                AddField("规则名称", _name, ref y);
                AddField("进程匹配（* 通配）", _pattern, ref y);
                AddField("动作", _action, ref y);
                AddField("目标优先级（-1 不变）", _priority, ref y);
                AddField("亲和性掩码（hex）", _mask, ref y);

                Controls.Add(new Label
                {
                    Text = "动作留空时按目标优先级执行；选 affinity 必须填掩码，例如 0xF。",
                    ForeColor = Theme.TextMuted,
                    Font = Theme.FontSmall,
                    AutoSize = false,
                    Location = new Point(Theme.S(20), y),
                    Size = new Size(Theme.S(440), Theme.S(34))
                });

                var ok = UiToolkit.CreatePrimaryButton("确定");
                ok.Location = new Point(ClientSize.Width - Theme.S(200), ClientSize.Height - Theme.S(52));
                ok.DialogResult = DialogResult.OK;
                var cancel = UiToolkit.CreateButton("取消");
                cancel.Location = new Point(ClientSize.Width - Theme.S(100), ClientSize.Height - Theme.S(52));
                cancel.DialogResult = DialogResult.Cancel;
                Controls.Add(ok);
                Controls.Add(cancel);
                AcceptButton = ok;
                CancelButton = cancel;

                if (existing != null)
                {
                    int index = _action.Items.IndexOf(existing.NormalizedAction() switch
                    {
                        "priority" => "priority（改优先级）",
                        "affinity" => "affinity（绑核心）",
                        "preload" => "preload（预加载）",
                        _ => "不指定动作"
                    });
                    _action.SelectedIndex = index >= 0 ? index : 0;
                }
                else _action.SelectedIndex = 0;
            }

            public string RuleName => _name.Text.Trim();

            public string Pattern => _pattern.Text.Trim();

            public int TargetPriority => (int)_priority.Value;

            public string Action
            {
                get
                {
                    string text = _action.SelectedIndex switch
                    {
                        1 => "priority",
                        2 => "affinity",
                        3 => "preload",
                        _ => ""
                    };
                    return text;
                }
            }

            public long AffinityMask
            {
                get
                {
                    string text = _mask.Text.Trim();
                    if (text.Length == 0) return -1;
                    try
                    {
                        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            return Convert.ToInt64(text.Substring(2), 16);
                        return long.Parse(text);
                    }
                    catch { return -2; }
                }
            }

            private static TextBox NewText(string value) => new()
            {
                Text = value,
                BackColor = Theme.Surface,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontBody
            };

            private void AddField(string label, Control control, ref int y)
            {
                Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point(Theme.S(20), y + Theme.S(5)),
                    AutoSize = true,
                    ForeColor = Theme.TextSecondary
                });
                control.Location = new Point(Theme.S(160), y);
                control.Size = new Size(Theme.S(300), Theme.S(28));
                Controls.Add(control);
                y += Theme.S(44);
            }
        }
    }
}
