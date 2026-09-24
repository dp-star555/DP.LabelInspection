using System;
using System.Drawing;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>结果区顶部的判定徽标及状态消息；徽标只反映最近一次检测，不代表整标签认证。</summary>
internal sealed class VerdictBanner : TableLayoutPanel
{
    private readonly Label _badge = new Label
    {
        AutoSize = true,
        MinimumSize = new Size(76, 0),
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(0, 0, 8, 0),
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
    };
    private readonly Label _message = new Label
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
    };
    private readonly ToolTip _tips = new ToolTip();
    private Font? _badgeFont;

    internal VerdictBanner()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 2;
        Padding = new Padding(6, 6, 6, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_badge, 0, 0);
        Controls.Add(_message, 1, 0);
        ApplyBadgeFont();
        ShowIdle();
    }

    /// <summary>状态消息；过长时省略，完整内容见提示。</summary>
    internal string Message
    {
        get => _message.Text;
        set
        {
            _message.Text = value;
            _tips.SetToolTip(_message, value);
        }
    }

    /// <summary>没有当前检测结果。</summary>
    internal void ShowIdle()
    {
        SetBadge("未检测", Color.FromArgb(117, 117, 117));
    }

    /// <summary>检测进行中。</summary>
    internal void ShowRunning()
    {
        SetBadge("检测中", Color.FromArgb(21, 101, 192));
    }

    /// <summary>显示最近一次完整报告的综合判定。</summary>
    internal void ShowVerdict(EInspectionVerdict verdict)
    {
        switch (verdict)
        {
            case EInspectionVerdict.Ok:
                SetBadge("OK", Color.FromArgb(46, 125, 50));
                break;
            case EInspectionVerdict.Ng:
                SetBadge("NG", Color.FromArgb(198, 40, 40));
                break;
            default:
                SetBadge("复核", Color.FromArgb(230, 100, 0));
                break;
        }
    }

    /// <summary>检测被取消，没有生成结果。</summary>
    internal void ShowCancelled()
    {
        SetBadge("已取消", Color.FromArgb(109, 76, 65));
    }

    /// <summary>检测未完成，没有生成结果。</summary>
    internal void ShowFailed()
    {
        SetBadge("失败", Color.FromArgb(136, 14, 79));
    }

    private void SetBadge(string text, Color color)
    {
        _badge.Text = text;
        _badge.BackColor = color;
    }

    /// <inheritdoc/>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyBadgeFont();
    }

    private void ApplyBadgeFont()
    {
        var previous = _badgeFont;
        _badgeFont = new Font(Font, FontStyle.Bold);
        _badge.Font = _badgeFont;
        previous?.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _tips.Dispose();
            _badgeFont?.Dispose();
            _badgeFont = null;
        }
    }
}
