using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>
/// 一个单字的比较结果卡片：标题为ROI/单字/状态，其下一行给出差异多少（差异比与上限、缺墨/多墨像素），
/// 再下为带标题的原始/参考/归一实际/差异小图及操作按钮。<see cref="Control.Tag"/>为（ROI名称, 单字证据）。
/// </summary>
internal sealed class GlyphResultCard : FlowLayoutPanel
{
    private static readonly ModernTheme Theme = ModernTheme.Dark;
    private const int TileWidth = 76, TileHeight = 96;

    internal GlyphResultCard(string region, GlyphInspection glyph, double? limit)
    {
        Tag = Tuple.Create(region, glyph);
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(8, 6, 8, 6);
        Margin = new Padding(4);
        BackColor = Theme.Container;
        var comparison = glyph.Comparison;
        bool exceeded = glyph.Status == "exceeds_threshold";
        bool compared = comparison != null && comparison.Status == "compared";
        Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = exceeded ? Theme.Error : compared ? Theme.Text : Theme.Warning,
            Margin = new Padding(0, 0, 0, 2),
            Text = region + " / “" + glyph.Character.Character + "” · " + StatusText(glyph.Status),
        });
        Controls.Add(new Label
        {
            Name = "GlyphDifference",
            AutoSize = true,
            MaximumSize = new Size(4 * (LogicalToDeviceUnits(TileWidth) + 4), 0),
            ForeColor = exceeded ? Theme.Error : Theme.TextSecondary,
            Margin = new Padding(0, 0, 0, 4),
            Text = DifferenceText(glyph, limit),
        });
        var tiles = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        tiles.Controls.Add(Tile(glyph.Character.Patch, "原始单字"));
        if (comparison != null)
        {
            tiles.Controls.Add(Tile(comparison.Reference, "参考"));
            tiles.Controls.Add(Tile(comparison.Actual, "归一实际"));
            tiles.Controls.Add(Tile(comparison.Delta, "差异"));
        }

        Controls.Add(tiles);
        Actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        Controls.Add(Actions);
    }

    /// <summary>卡片底部的操作按钮行。</summary>
    internal FlowLayoutPanel Actions { get; }

    /// <summary>差异描述：比较了多少、离上限多远；未比较时说明原因。</summary>
    internal static string DifferenceText(GlyphInspection glyph, double? limit)
    {
        var c = glyph.Comparison;
        if (c == null || c.Status != "compared")
        {
            return glyph.Status switch
            {
                "missing_template" => "未比较：字库中没有此字的参考",
                "uncertain_identity" => "未比较：OCR身份不确定",
                "empty_reference" => "未比较：参考为空白",
                _ => "未比较",
            };
        }

        string ratio = c.Difference.ToString("P1", CultureInfo.CurrentCulture);
        string bound = limit.HasValue
            ? "（上限 " + limit.Value.ToString("P1", CultureInfo.CurrentCulture)
                + (c.Difference > limit.Value
                    ? "，超出 " + (c.Difference - limit.Value).ToString("P1", CultureInfo.CurrentCulture)
                    : "，余量 " + (limit.Value - c.Difference).ToString("P1", CultureInfo.CurrentCulture))
                + "）"
            : "";
        return "差异 " + ratio + bound + Environment.NewLine
            + "缺墨 " + c.Missing + " px · 多墨 " + c.Extra + " px（归一化像素）";
    }

    private static string StatusText(string status) => status switch
    {
        "compared" => "合格",
        "exceeds_threshold" => "超差",
        "missing_template" => "缺参考",
        "uncertain_identity" => "身份不确定",
        "empty_reference" => "空参考",
        _ => status,
    };

    private Control Tile(PixelSnapshot frame, string caption)
    {
        var tile = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 4, 0),
        };
        tile.Controls.Add(new PictureBox
        {
            // 小图与标题按DPI换算，避免标题被截断。
            Width = LogicalToDeviceUnits(TileWidth),
            Height = LogicalToDeviceUnits(TileHeight),
            Image = DrawingImageConverter.ToBitmap(frame),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Background,
            AccessibleName = caption,
            Margin = Padding.Empty,
        });
        tile.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = false,
            Width = LogicalToDeviceUnits(TileWidth),
            Height = LogicalToDeviceUnits(22),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextSecondary,
            Margin = Padding.Empty,
        });
        return tile;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var picture in Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<FlowLayoutPanel>())
                .SelectMany(t => t.Controls.OfType<PictureBox>()))
            {
                picture.Image?.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
