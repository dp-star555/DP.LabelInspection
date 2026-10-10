using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>所选证据的ROI图像与结论（上）及完整子项明细（下）；只读，不重复展示原图面板。</summary>
internal sealed class BarcodeComparisonControl : UserControl
{
    internal BarcodeComparisonControl(
        PixelSnapshot actual,
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
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        // 结论行：ROI、判定与汇总说明；右侧为视图按钮。操作提示放在工具提示中，不占版面。
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var verdict = group.Summary.Verdict;
        header.Controls.Add(
            new Label
            {
                Name = "EvidenceConclusion",
                Text = group.RegionName + " · " + group.Status + " · " + group.Summary.Message,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor =
                    verdict == EInspectionVerdict.Ng ? ModernUI.WinForms.ModernTheme.Dark.Error
                    : verdict == EInspectionVerdict.Review ? ModernUI.WinForms.ModernTheme.Dark.Warning
                    : ModernUI.WinForms.ModernTheme.Dark.Success,
            },
            0,
            0
        );
        var toolbar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        header.Controls.Add(toolbar, 1, 0);
        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        tips.SetToolTip(marked, "滚轮缩放，中/右键平移；双击下方明细放大到该处");
        var fit = InspectionUiStyle.CreateButton("适应");
        fit.Click += (_, _) => marked.FitToWindow();
        toolbar.Controls.Add(fit);
        var pixel = InspectionUiStyle.CreateButton("1:1");
        pixel.Click += (_, _) => marked.ActualSize();
        toolbar.Controls.Add(pixel);
        if (allowExpand)
        {
            var expand = InspectionUiStyle.CreateButton("独立放大");
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

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(marked, 0, 1);
        var details = new ModernUI.WinForms.ModernListView
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
        layout.Controls.Add(details, 0, 2);
        Controls.Add(layout);
    }
}
