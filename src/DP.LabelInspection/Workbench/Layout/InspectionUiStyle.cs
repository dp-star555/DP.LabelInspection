using System;
using System.Drawing;
using System.Windows.Forms;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>标签工作台及其编辑窗口共享的现代深色外观；不依赖宿主的空闲事件补色。</summary>
internal static class InspectionUiStyle
{
    internal static void Apply(Control root)
    {
        ModernUiSettings.ApplyTheme(root, ModernTheme.Dark);
        PrepareButtons(root);
    }

    internal static ModernButton CreateButton(string text)
    {
        var button = new ModernButton { Text = text, Theme = ModernTheme.Dark, AutoSize = true };
        PrepareButton(button);
        return button;
    }

    /// <summary>按控件DPI换算的按钮高度；与 <see cref="CreateButton"/> 的按钮同高，供同一行的复选框等对齐。</summary>
    internal static int ButtonHeight(Control control)
    {
        var scale = Math.Max(1, control.DeviceDpi) / 96f;
        var text = TextRenderer.MeasureText("字", control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return Math.Max((int)(34 * scale), text.Height + (int)(14 * scale));
    }

    /// <summary>让与按钮同排的控件保持按钮高度，DPI或字体变化后重新计算，使其在行内垂直居中。</summary>
    internal static void MatchButtonHeight(Control control)
    {
        void Fit(object? sender, EventArgs args) => control.Height = ButtonHeight(control);
        control.HandleCreated += Fit;
        control.FontChanged += Fit;
        control.DpiChangedAfterParent += Fit;
        Fit(null, EventArgs.Empty);
    }

    /// <summary>
    /// 统一工具栏内嵌下拉框/输入框的尺寸：宽度取创建时的逻辑宽度，高度统一为32逻辑像素，二者都随DPI换算。
    /// 宿主与内嵌控件必须同尺寸，否则控件会按自身最小尺寸溢出或被裁切，表现为高低不一。
    /// </summary>
    internal static void FitToolStrip(ToolStrip strip)
    {
        const int ControlHeight = 32;
        void Fit(object? sender, EventArgs args)
        {
            var scale = Math.Max(1, strip.DeviceDpi) / 96f;
            int Scale(int logical) => (int)Math.Round(logical * scale);
            strip.SuspendLayout();
            try
            {
                strip.Padding = new Padding(Scale(6), Scale(3), Scale(6), Scale(3));
                foreach (ToolStripItem item in strip.Items)
                {
                    if (item is not ToolStripControlHost host) continue;
                    if (host.Tag is not int width)
                        host.Tag = width = host.Control.Width;
                    var size = new Size(Scale(width), Scale(ControlHeight));
                    host.AutoSize = false;
                    host.Control.MinimumSize = Size.Empty;
                    host.Control.Size = size;
                    host.Size = size;
                    host.Margin = new Padding(Scale(2), 0, Scale(2), 0);
                }
            }
            finally
            {
                strip.ResumeLayout(true);
            }
        }
        strip.HandleCreated += Fit;
        strip.FontChanged += Fit;
        strip.DpiChangedAfterParent += Fit;
        Fit(null, EventArgs.Empty);
    }

    private static void PrepareButtons(Control root)
    {
        if (root is ModernButton button && button.AutoSize)
            PrepareButton(button);
        foreach (Control child in root.Controls)
            PrepareButtons(child);
    }

    // ModernButton 不提供原生 Button 的 AutoSize 测量；FlowLayoutPanel 会把它缩成零尺寸。
    // 按当前字体显式测量宽度，字体/DPI或文字变化时重新布局，而不是依赖零尺寸的首选大小。
    private static void PrepareButton(ModernButton button)
    {
        button.AutoSize = false;
        void ResizeButton(object? sender, EventArgs args)
        {
            var scale = Math.Max(1, button.DeviceDpi) / 96f;
            var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            button.Size = new Size(
                Math.Max((int)(80 * scale), text.Width + (int)(24 * scale)),
                Math.Max((int)(34 * scale), text.Height + (int)(14 * scale)));
        }
        button.FontChanged += ResizeButton;
        button.TextChanged += ResizeButton;
        ResizeButton(null, EventArgs.Empty);
    }
}
