using System;
using System.Drawing;
using System.Windows.Forms;

namespace DP.LabelInspection;

/// <summary>画布上方的视图工具条：适应、1:1、缩放及当前像素比例，只改变显示不改变坐标。</summary>
internal sealed class CanvasViewBar : FlowLayoutPanel
{
    private readonly ImageViewerControl _viewer;
    private readonly Label _scale = new Label { AutoSize = true, Margin = new Padding(8, 7, 8, 0) };

    internal CanvasViewBar(ImageViewerControl viewer)
    {
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(4, 2, 4, 2);
        AddButton("适应窗口", () => _viewer.FitToWindow());
        AddButton("1:1", () => _viewer.ActualSize());
        AddButton("缩小−", () => _viewer.ZoomAt(.8f, Center()));
        AddButton("放大+", () => _viewer.ZoomAt(1.25f, Center()));
        Controls.Add(_scale);
        Controls.Add(
            new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 7, 0, 0),
                Text = "滚轮缩放 · 中键/右键拖动平移 · Home复位 · 左键拖动绘制ROI",
            }
        );
        _viewer.ViewChanged += OnViewChanged;
        UpdateScale();
    }

    private void AddButton(string text, Action action)
    {
        Controls.Add(ActionGroup.CreateButton(text, action));
    }

    private Point Center()
    {
        return new Point(_viewer.Width / 2, _viewer.Height / 2);
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        UpdateScale();
    }

    private void UpdateScale()
    {
        _scale.Text = $"{_viewer.ImageScale * 100:0.#}%";
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewer.ViewChanged -= OnViewChanged;
        }

        base.Dispose(disposing);
    }
}
