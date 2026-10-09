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
