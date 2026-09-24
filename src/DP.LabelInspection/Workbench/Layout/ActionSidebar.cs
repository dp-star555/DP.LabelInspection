using System.Drawing;
using System.Windows.Forms;

namespace DP.LabelInspection;

/// <summary>纵向操作侧栏；宽度取各组所需宽度，高度不足时纵向滚动，不横向裁切按钮。</summary>
internal sealed class ActionSidebar : Panel
{
    private readonly TableLayoutPanel _stack = new TableLayoutPanel
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Margin = Padding.Empty,
    };

    internal ActionSidebar()
    {
        AutoScroll = true;
        Padding = new Padding(10, 10, 6, 10);
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _stack.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(_stack);
    }

    /// <summary>在侧栏末尾追加一组操作。</summary>
    internal ActionGroup AddGroup(string title)
    {
        var group = new ActionGroup(title) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _stack.Controls.Add(group);
        return group;
    }

    /// <inheritdoc/>
    public override Size GetPreferredSize(Size proposedSize)
    {
        // 始终预留纵向滚动条宽度，避免高度变化时滚动条出现导致按钮被横向裁切。
        var content = _stack.GetPreferredSize(Size.Empty);
        return new Size(
            content.Width + Padding.Horizontal + SystemInformation.VerticalScrollBarWidth,
            content.Height + Padding.Vertical
        );
    }
}
