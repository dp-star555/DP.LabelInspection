using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

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
        var text = new TextBox { Text = value, Dock = DockStyle.Top };
        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
        };
        form.Controls.Add(text);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? text.Text : null;
    }
}
