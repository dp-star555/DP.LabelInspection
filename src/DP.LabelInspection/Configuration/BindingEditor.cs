using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using EBindingSource = DP.LabelInspection.Contracts.EBindingSource;

namespace DP.LabelInspection;

internal static class BindingEditor
{
    internal static IReadOnlyList<FieldBinding>? Edit(
        IEnumerable<InspectionRegion> regions,
        IEnumerable<FieldBinding> bindings
    )
    {
        using var form = new Form
        {
            Text = "字段绑定：Region为其他ROI原始读数；TaskData为本次任务字段",
            Width = 850,
            Height = 430,
            StartPosition = FormStartPosition.CenterParent,
        };
        var names = regions
            .Where(r => r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode)
            .Select(r => r.Name)
            .ToArray();
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
        };
        var target = new DataGridViewComboBoxColumn { HeaderText = "待校验ROI" };
        target.Items.AddRange(names);
        grid.Columns.Add(target);
        var source = new DataGridViewComboBoxColumn { HeaderText = "来源类型" };
        source.Items.AddRange(Enum.GetNames(typeof(EBindingSource)));
        grid.Columns.Add(source);
        grid.Columns.Add("key", "来源ROI名称 / 任务字段名（精确匹配）");
        foreach (var binding in bindings)
        {
            grid.Rows.Add(binding.Target, binding.Source.ToString(), binding.Key);
        }

        grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
        };
        IReadOnlyList<FieldBinding>? result = null;
        var save = new Button
        {
            Text = "保存绑定（删除行可解绑）",
            Dock = DockStyle.Bottom,
            Height = 36,
        };
        save.Click += (_, _) =>
        {
            try
            {
                grid.EndEdit();
                var list = grid
                    .Rows.Cast<DataGridViewRow>()
                    .Where(r => !r.IsNewRow)
                    .Select(r => new FieldBinding(
                        (string)r.Cells[0].Value!,
                        (EBindingSource)Enum.Parse(typeof(EBindingSource), (string)r.Cells[1].Value!),
                        (string)r.Cells[2].Value!
                    ))
                    .ToArray();
                if (
                    list.Any(b =>
                        !names.Contains(b.Target)
                        || (b.Source == EBindingSource.Region && !names.Contains(b.Key))
                    )
                )
                {
                    throw new ArgumentException("绑定名称必须是现有文字/条码ROI。");
                }

                result = list;
                form.DialogResult = DialogResult.OK;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "绑定未保存");
            }
        };
        form.Controls.Add(grid);
        form.Controls.Add(save);
        return form.ShowDialog() == DialogResult.OK ? result : null;
    }

    internal static TaskDataSnapshot? TaskData()
    {
        using var form = new Form
        {
            Text = "本次图像任务数据（人工调试输入；切换待检图即清空）",
            Width = 650,
            Height = 480,
            StartPosition = FormStartPosition.CenterParent,
        };
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        var cycle = new TextBox { Width = 590 };
        var source = new TextBox { Width = 590, Text = "manual-debug" };
        var minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Value = 10,
        };
        var values = new TextBox
        {
            Multiline = true,
            Width = 590,
            Height = 210,
            ScrollBars = ScrollBars.Vertical,
        };
        panel.Controls.AddRange(
            new Control[]
            {
                new Label { Text = "检测周期编号", AutoSize = true },
                cycle,
                new Label { Text = "来源标识", AutoSize = true },
                source,
                new Label { Text = "有效分钟数", AutoSize = true },
                minutes,
                new Label { Text = "每行 字段名=值；不自动删除值的空格，不纠正O/0", AutoSize = true },
                values,
            }
        );
        TaskDataSnapshot? result = null;
        var save = new Button
        {
            Text = "确认本次任务数据",
            Dock = DockStyle.Bottom,
            Height = 35,
        };
        save.Click += (_, _) =>
        {
            try
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in values.Lines.Where(l => l.Length > 0))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0)
                    {
                        throw new ArgumentException("字段格式必须为 key=value。");
                    }

                    fields.Add(line.Substring(0, i), line.Substring(i + 1));
                }

                var now = DateTimeOffset.UtcNow;
                result = new TaskDataSnapshot(
                    cycle.Text,
                    source.Text,
                    now,
                    now.AddMinutes((double)minutes.Value),
                    fields
                );
                form.DialogResult = DialogResult.OK;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "任务数据未保存");
            }
        };
        form.Controls.Add(panel);
        form.Controls.Add(save);
        return form.ShowDialog() == DialogResult.OK ? result : null;
    }
}
