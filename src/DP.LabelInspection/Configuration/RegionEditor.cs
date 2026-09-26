using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

internal static partial class RegionEditor
{
    internal static InspectionRegion[]? Edit(
        IEnumerable<InspectionRegion> regions,
        IGlyphLibraryManager? manager,
        IAnomalyLibraryManager? anomalyManager = null
    )
    {
        using var form = new Form
        {
            Text = "ROI配置 · 固定内容与可变文字分开设置",
            Width = 950,
            Height = 680,
            StartPosition = FormStartPosition.CenterParent,
        };
        form.MinimumSize = new Size(850, 680);
        var list = new ListBox { Dock = DockStyle.Left, Width = 230 };
        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            HelpVisible = false,
            PropertySort = PropertySort.CategorizedAlphabetical,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var help = new TextBox
        {
            Name = "RoiParameterHelp",
            Dock = DockStyle.Bottom,
            Height = 130,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(238, 244, 252),
            Font = new Font("Microsoft YaHei UI", 10),
            Text =
                "选中右侧参数，可在此查看用途、适用范围、单位、取值范围和调整影响。修改后点击“保存配置”才应用；关闭窗口不应用修改。",
        };
        void Describe()
        {
            var property = grid.SelectedGridItem?.PropertyDescriptor;
            help.Text =
                property == null
                    ? "选中参数查看说明。修改后点击“保存配置”才应用。"
                    : property.DisplayName + "\r\n" + property.Description;
        }

        grid.SelectedGridItemChanged += (_, _) => Describe();
        grid.PropertyValueChanged += (_, _) =>
        {
            Describe();
            list.Refresh();
        };
        var values = regions.Select(r => new EditableRegion(r)).ToList();
        foreach (var value in values)
        {
            list.Items.Add(value);
        }

        list.SelectedIndexChanged += (_, _) => grid.SelectedObject = list.SelectedItem;
        var libraries = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
        if (manager != null)
        {
            foreach (var item in manager.ListLibraries())
            {
                libraries.Items.Add(item);
            }
        }

        var bind = new Button { Text = "绑定所选类别/版本", AutoSize = true };
        bind.Click += (_, _) =>
        {
            if (list.SelectedItem is EditableRegion r && libraries.SelectedItem is GlyphLibraryInfo l)
            {
                r.LibraryId = l.Id;
                r.LibraryRevision = l.Revision;
                grid.Refresh();
            }
        };
        var anomalyLibraries = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        if (anomalyManager != null)
        {
            foreach (var item in anomalyManager.ListAnomalyLibraries())
            {
                anomalyLibraries.Items.Add(item);
            }
        }

        var bindAnomaly = new Button { Text = "绑定所选异常模型库/版本", AutoSize = true };
        bindAnomaly.Click += (_, _) =>
        {
            if (
                !(list.SelectedItem is EditableRegion r)
                || !(anomalyLibraries.SelectedItem is AnomalyLibraryInfo l)
                || anomalyManager == null
            )
            {
                return;
            }

            r.AnomalyLibraryId = l.Id;
            r.AnomalyLibraryRevision = l.Revision;
            // 按库中实际内容选择模式：有本ROI的整ROI模型用整ROI；文字ROI且库中是字符模型则选逐字符；都没有时立即提示。
            var models = anomalyManager.LoadAnomalyLibrary(l.Id, l.Revision).Models;
            string key = string.IsNullOrWhiteSpace(r.AnomalyModelKey) ? r.Name : r.AnomalyModelKey!;
            bool whole = models.TryGetValue(key, out var own) && own.Scope == EAnomalyModelScope.Region;
            // 字符模型按字符组（null为不分组）归类；优先与模型键/ROI同名的组，其次不分组，再次唯一的组。
            var groups = models
                .Values.Where(m => m.Scope == EAnomalyModelScope.Character)
                .GroupBy(m => m.Group ?? "")
                .ToDictionary(
                    g => g.Key,
                    g => string.Concat(g.Select(m => m.Character).OrderBy(c => c, StringComparer.Ordinal))
                );
            string? group =
                groups.ContainsKey(key) ? key
                : groups.ContainsKey("") ? ""
                : groups.Count == 1 ? groups.Keys.Single()
                : null;
            string Describe(string g) => (g.Length == 0 ? "不分组" : "组[" + g + "]") + "：" + groups[g];
            string note;
            if (whole)
            {
                r.AnomalyPerCharacter = false;
                note = $"已绑定 {l.Name} r{l.Revision}：整ROI模型[{key}]。";
            }
            else if (r.Kind == ERegionKind.Text && group != null)
            {
                r.AnomalyPerCharacter = true;
                r.AnomalyModelKey = group.Length == 0 ? null : group;
                note =
                    $"已绑定 {l.Name} r{l.Revision}：字符模型（{Describe(group)}），已自动选择“逐字符检查”（需要OCR）。"
                    + (groups.Count > 1 ? "库中还有其他字符组，可在“模型键”中改填。" : "");
            }
            else
            {
                var regions = models
                    .Values.Where(m => m.Scope == EAnomalyModelScope.Region)
                    .Select(m => m.Key)
                    .ToArray();
                note =
                    $"注意：{l.Name} r{l.Revision} 中没有模型[{key}]，检测时本ROI的B会判NG。"
                    + (
                        regions.Length == 0
                            ? "库中没有整ROI模型。"
                            : "库中现有整ROI模型：" + string.Join("、", regions) + "（可在“模型键”中填写）。"
                    )
                    + (
                        groups.Count == 0 ? ""
                        : r.Kind == ERegionKind.Text
                            ? "库中有多个字符组（"
                                + string.Join(
                                    "；",
                                    groups.Keys.OrderBy(g => g, StringComparer.Ordinal).Select(Describe)
                                )
                                + "），请选“逐字符检查”并在“模型键”中填写字符组。"
                        : "字符模型只能用于文字ROI的逐字符检查。"
                    )
                    + "请先在“批量训练(B)”中为本ROI训练并发布。";
                MessageBox.Show(form, note, "异常模型库中没有此ROI的模型");
            }

            help.Text = note;
            grid.Refresh();
        };
        var remove = new Button { Text = "删除选中ROI", AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is EditableRegion r)
            {
                values.Remove(r);
                list.Items.Remove(r);
                grid.SelectedObject = null;
            }
        };
        var save = new Button { Text = "保存配置", AutoSize = true };
        InspectionRegion[]? result = null;
        save.Click += (_, _) =>
        {
            try
            {
                result = values.Select(v => v.Build()).ToArray();
                if (result.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != result.Length)
                {
                    throw new ArgumentException("ROI名称必须唯一。");
                }

                form.DialogResult = DialogResult.OK;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "配置无效");
            }
        };
        var cancel = new Button
        {
            Text = "取消修改",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        form.CancelButton = cancel;
        buttons.Controls.AddRange(
            new Control[] { libraries, bind, anomalyLibraries, bindAnomaly, remove, save, cancel }
        );
        form.Controls.Add(grid);
        form.Controls.Add(list);
        form.Controls.Add(help);
        form.Controls.Add(buttons);
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        return form.ShowDialog() == DialogResult.OK ? result : null;
    }
}
