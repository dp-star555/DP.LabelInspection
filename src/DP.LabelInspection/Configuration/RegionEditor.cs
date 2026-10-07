using System;
using System.Collections.Generic;
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
        var view = new RegionRulesView(
            "选中参数，可在此查看用途、适用范围、单位、取值范围和调整影响。修改后点击“保存配置”才应用；关闭窗口不应用修改。"
        )
        {
            Dock = DockStyle.Fill,
        };
        view.SetRegions(regions, manager, anomalyManager);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        InspectionRegion[]? result = null;
        var save = new Button { Text = "保存配置", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                result = view.Build();
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
        buttons.Controls.AddRange(new Control[] { save, cancel });
        form.Controls.Add(view);
        form.Controls.Add(buttons);
        return form.ShowDialog() == DialogResult.OK ? result : null;
    }
}
