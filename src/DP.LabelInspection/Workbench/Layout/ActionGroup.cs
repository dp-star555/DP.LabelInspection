using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DP.LabelInspection;

/// <summary>侧栏中带标题的一组操作；子控件纵向排列并拉伸到同一宽度，随宿主字体缩放。</summary>
internal sealed class ActionGroup : TableLayoutPanel
{
    private readonly Label _title;
    private readonly List<Control> _emphasized = new List<Control>();
    private readonly List<Font> _fonts = new List<Font>();

    internal ActionGroup(string title)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        Margin = new Padding(0, 0, 0, 12);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _title = new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2),
        };
        Controls.Add(_title);
        Controls.Add(
            new Label
            {
                AutoSize = false,
                Height = 1,
                BackColor = SystemColors.ControlDark,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 0, 0, 4),
            }
        );
        _emphasized.Add(_title);
        ApplyEmphasis();
    }

    /// <summary>追加一个拉伸到组宽度的控件。</summary>
    internal T Add<T>(T control)
        where T : Control
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 2, 0, 2);
        Controls.Add(control);
        return control;
    }

    /// <summary>追加操作按钮；操作异常以对话框提示，不向消息循环抛出。</summary>
    internal Button AddButton(string text, Action action)
    {
        return Add(CreateButton(text, action));
    }

    /// <summary>以粗体突出主要操作，字体变化后保持粗体。</summary>
    internal void Emphasize(Control control)
    {
        _emphasized.Add(control);
        ApplyEmphasis();
    }

    /// <summary>创建自动宽度按钮；操作异常以对话框提示，不向消息循环抛出。</summary>
    internal static Button CreateButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "操作未完成");
            }
        };
        return button;
    }

    /// <inheritdoc/>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyEmphasis();
    }

    private void ApplyEmphasis()
    {
        var bold = new Font(Font, FontStyle.Bold);
        foreach (var control in _emphasized)
        {
            control.Font = bold;
        }

        ReleaseFonts();
        _fonts.Add(bold);
    }

    private void ReleaseFonts()
    {
        foreach (var font in _fonts)
        {
            font.Dispose();
        }

        _fonts.Clear();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            ReleaseFonts();
        }
    }
}
