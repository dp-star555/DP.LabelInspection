using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernUI.WinForms;

namespace DP.LabelInspection;

internal static class EditorDialogs
{
    internal static string? Ask(string title, string value)
    {
        using var form = new Form
        {
            Text = title,
            Width = 560,
            Height = 160,
            StartPosition = FormStartPosition.CenterParent,
        };
        var text = new ModernInput { Text = value, Dock = DockStyle.Top };
        var ok = new ModernButton
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
        };
        form.Controls.Add(text);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        InspectionUiStyle.Apply(form);
        return form.ShowDialog() == DialogResult.OK ? text.Text : null;
    }
}
