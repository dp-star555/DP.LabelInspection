using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>
/// 所选证据的结论行、ROI图像（按ROI宽高比限高，滚轮缩放、中/右键平移）及子项明细表；只读，不重复展示原图面板。
/// 宿主按<see cref="PreferredHeight"/>给出高度。
/// </summary>
internal sealed class BarcodeComparisonControl : UserControl
{
    private const int MaximumImageHeight = 180, MinimumImageHeight = 70, MaximumVisibleRows = 6;
    private readonly PixelRect? _bounds;
    private readonly ModernListView? _details;
    private readonly Label? _conclusion;

    internal BarcodeComparisonControl(PixelSnapshot actual, InspectionEvidenceGroup group)
    {
        Name = group.IsBarcode ? "BarcodeComparison" : "RoiComparison";
        Size = new Size(1000, 400);
        BackColor = ModernTheme.Dark.Background;
        if (!group.Summary.Bounds.HasValue || !group.Summary.Bounds.Value.Fits(actual))
        {
            Controls.Add(new Label
            {
                Text = "ROI范围不可用，完整明细仍保留在报告中。",
                AutoSize = true,
                ForeColor = ModernTheme.Dark.TextSecondary,
            });
            return;
        }

        var bounds = group.Summary.Bounds.Value;
        _bounds = bounds;
        var crop = actual.Crop(bounds);
        var marked = new ImageViewerControl
        {
            Dock = DockStyle.Fill,
            Name = group.IsBarcode ? "BarcodeMarked" : "RoiMarked",
            ShowFindingLabels = false,
            AllowRegionDrawing = false,
        };
        marked.SetImage(crop);
        var defects = group
            .Children.Where(c =>
                c.Finding.Verdict != EInspectionVerdict.Ok
                && (c.IsLocalizedCandidate || c.Finding.Code == "barcode_not_decoded")
                && c.Finding.Bounds.HasValue
                && c.Finding.Bounds.Value.Fits(actual)
            )
            .ToArray();
        var mapped = defects
            .Select(c => new InspectionFinding(
                c.Finding.Code,
                c.Finding.Message,
                c.Finding.Verdict,
                new PixelRect(
                    c.Finding.Bounds!.Value.X - bounds.X,
                    c.Finding.Bounds.Value.Y - bounds.Y,
                    c.Finding.Bounds.Value.Width,
                    c.Finding.Bounds.Value.Height
                ),
                c.Finding.AreaPixels
            ))
            .ToArray();
        marked.SetOverlays(Array.Empty<InspectionRegion>(), mapped);

        // 结论行：ROI、判定与汇总说明；过长时省略，悬停查看全文。
        string conclusion = group.RegionName + " · " + group.Status + " · " + group.Summary.Message;
        _conclusion = new Label
        {
            Name = "EvidenceConclusion",
            Text = conclusion,
            Dock = DockStyle.Top,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 4, 0),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = LabelInspectionControl.VerdictColor(group.Summary.Verdict),
        };
        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        tips.SetToolTip(_conclusion, conclusion);

        _details = new ModernListView
        {
            Name = group.IsBarcode ? "BarcodeDefectDetails" : "RoiEvidenceDetails",
            Dock = DockStyle.Bottom,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            ShowItemToolTips = true,
            Theme = ModernTheme.Dark,
        };
        int S(int logical) => LogicalToDeviceUnits(logical);
        _details.Columns.Add("子项", S(80));
        _details.Columns.Add("结论", S(70));
        _details.Columns.Add("检查", S(190));
        _details.Columns.Add("面积px²", S(80));
        _details.Columns.Add("说明", S(600));
        foreach (var child in group.Children)
        {
            var f = child.Finding;
            var item = new ListViewItem(new[] { child.Id, child.Status, f.Code, f.AreaPixels?.ToString() ?? "", f.Message })
            {
                Tag = child,
                ToolTipText = f.Code + "：" + f.Message,
                UseItemStyleForSubItems = false,
            };
            item.SubItems[1].ForeColor = LabelInspectionControl.VerdictColor(f.Verdict);
            _details.Items.Add(item);
        }

        // 说明列占满剩余宽度，尽量完整显示。
        _details.Resize += (_, _) =>
        {
            int used = 0;
            for (int i = 0; i < _details.Columns.Count - 1; i++)
            {
                used += _details.Columns[i].Width;
            }

            _details.Columns[_details.Columns.Count - 1].Width = Math.Max(S(200), _details.ClientSize.Width - used - S(4));
        };
        _details.DoubleClick += (_, _) =>
        {
            if (
                _details.SelectedItems.Count == 1
                && _details.SelectedItems[0].Tag is InspectionEvidenceDetail child
                && child.Finding.Bounds.HasValue
            )
            {
                var b = child.Finding.Bounds.Value;
                marked.FocusRegion(new PixelRect(b.X - bounds.X, b.Y - bounds.Y, b.Width, b.Height));
            }
        };
        marked.FindingSelected += (_, e) =>
        {
            if (e.Index < defects.Length)
            {
                foreach (ListViewItem item in _details.Items)
                {
                    if (ReferenceEquals(item.Tag, defects[e.Index]))
                    {
                        item.Selected = true;
                        item.EnsureVisible();
                        break;
                    }
                }
            }
        };

        // 停靠按Z序倒序处理：结论在上，明细在下，图像占中间。
        Controls.Add(marked);
        Controls.Add(_details);
        Controls.Add(_conclusion);
        Layout += (_, _) => ArrangeRows();
    }

    /// <summary>
    /// 给定宽度下的高度：结论行＋按ROI宽高比限高的图像＋最多若干行明细；超过<paramref name="available"/>时先压缩图像，
    /// 为下方单字卡片留出空间。
    /// </summary>
    internal int PreferredHeight(int width, int available)
    {
        if (_bounds == null)
        {
            return LogicalToDeviceUnits(40);
        }

        int fixedRows = ConclusionHeight + DetailsHeight;
        int image = Math.Min(ImageHeight(width), Math.Max(LogicalToDeviceUnits(MinimumImageHeight), available - fixedRows));
        return fixedRows + image;
    }

    private int ConclusionHeight => LogicalToDeviceUnits(30);

    private int DetailsHeight =>
        _details == null
            ? 0
            : LogicalToDeviceUnits(34)
                + LogicalToDeviceUnits(_details.RowHeight) * Math.Max(1, Math.Min(MaximumVisibleRows, _details.Items.Count))
                + LogicalToDeviceUnits(4);

    private int ImageHeight(int width)
    {
        var bounds = _bounds!.Value;
        int natural = (int)Math.Round((double)Math.Max(1, width) * bounds.Height / Math.Max(1, bounds.Width));
        return Math.Max(LogicalToDeviceUnits(MinimumImageHeight), Math.Min(LogicalToDeviceUnits(MaximumImageHeight), natural));
    }

    private void ArrangeRows()
    {
        if (_conclusion != null && _conclusion.Height != ConclusionHeight)
        {
            _conclusion.Height = ConclusionHeight;
        }

        if (_details != null && _details.Height != DetailsHeight)
        {
            _details.Height = DetailsHeight;
        }
    }
}
