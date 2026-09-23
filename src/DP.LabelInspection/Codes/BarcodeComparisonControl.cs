using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>带完整子证据的只读条码缺陷视图，不重复展示原图面板。</summary>
internal sealed class BarcodeComparisonControl : UserControl
{
    internal BarcodeComparisonControl(
        ImageFrame actual,
        InspectionEvidenceGroup group,
        bool allowExpand = true
    )
    {
        Name = group.IsBarcode ? "BarcodeComparison" : "RoiComparison";
        Height = 400;
        Width = 1000;
        if (!group.Summary.Bounds.HasValue || !group.Summary.Bounds.Value.Fits(actual))
        {
            Controls.Add(new Label { Text = "ROI范围不可用，完整明细仍保留在报告中。", AutoSize = true });
            return;
        }

        var bounds = group.Summary.Bounds.Value;
        var crop = actual.Crop(bounds);
        var marked = new ImageViewerControl
        {
            Dock = DockStyle.Fill,
            Name = group.IsBarcode ? "BarcodeMarked" : "RoiMarked",
            ShowFindingLabels = false,
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
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        toolbar.Controls.Add(
            new Label
            {
                Text =
                    group.Id
                    + " · "
                    + group.Status
                    + " · "
                    + group.RegionName
                    + "；滚轮缩放，中/右键平移；双击明细放大",
                AutoSize = true,
                Padding = new Padding(0, 7, 0, 0),
            }
        );
        var fit = new Button { Text = "适应", AutoSize = true };
        fit.Click += (_, _) => marked.FitToWindow();
        toolbar.Controls.Add(fit);
        var pixel = new Button { Text = "1:1", AutoSize = true };
        pixel.Click += (_, _) => marked.ActualSize();
        toolbar.Controls.Add(pixel);
        if (allowExpand)
        {
            var expand = new Button { Text = "独立放大查看", AutoSize = true };
            expand.Click += (_, _) =>
            {
                using var window = new Form
                {
                    Text = group.Id + " 缺陷标记 / 子项明细",
                    Width = 1280,
                    Height = 780,
                    StartPosition = FormStartPosition.CenterParent,
                };
                window.Controls.Add(
                    new BarcodeComparisonControl(actual, group, false) { Dock = DockStyle.Fill }
                );
                window.ShowDialog(FindForm());
            };
            toolbar.Controls.Add(expand);
        }

        layout.Controls.Add(toolbar, 0, 0);
        string notice =
            !group.IsBarcode
                ? "ROI内部："
                    + group.LocalizedCandidateCount
                    + "个定位候选框 / "
                    + group.BlockingItemCount
                    + "条比对阻断记录。缺字、分割失败不伪造局部缺陷点；完整明细如下。"
            : defects.Any(d => d.Finding.AreaPixels.HasValue)
                ? "缺陷标记：仅显示检测到的候选框，完整记录见下方。"
            : defects.Any(d => d.Finding.Code == "barcode_not_decoded")
                ? "读码失败NG：标记整个码区；未取得可靠结构，不能伪造内部小缺陷位置。"
            : "没有局部墨迹框；请查看下方内容/质量明细。";
        layout.Controls.Add(new Label { Text = notice, AutoSize = true }, 0, 1);
        layout.Controls.Add(marked, 0, 2);
        var details = new ListView
        {
            Name = group.IsBarcode ? "BarcodeDefectDetails" : "RoiEvidenceDetails",
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        details.Columns.Add("子项", 70);
        details.Columns.Add("结论", 65);
        details.Columns.Add("检查", 165);
        details.Columns.Add("原图坐标", 150);
        details.Columns.Add("面积px²", 75);
        details.Columns.Add("说明", 650);
        foreach (var child in group.Children)
        {
            var f = child.Finding;
            details.Items.Add(
                new ListViewItem(
                    new[]
                    {
                        child.Id,
                        child.Status,
                        f.Code,
                        f.Bounds?.ToString() ?? "",
                        f.AreaPixels?.ToString() ?? "",
                        f.Message,
                    }
                )
                {
                    Tag = child,
                }
            );
        }

        details.DoubleClick += (_, _) =>
        {
            if (
                details.SelectedItems.Count == 1
                && details.SelectedItems[0].Tag is InspectionEvidenceDetail child
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
                foreach (ListViewItem item in details.Items)
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
        layout.Controls.Add(details, 0, 3);
        Controls.Add(layout);
    }
}
