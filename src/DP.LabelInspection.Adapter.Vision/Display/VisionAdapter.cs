using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;
using V = DP.Vision;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>单向业务适配接口；DP.Vision不依赖标签检测，现有标签公共类型保持不变。</summary>
public static class VisionAdapter
{
    /// <summary>将现有不可变检测图复制为通用独立图像，返回租约需由调用方释放。</summary>
    /// <param name = "image">待复制的不可变标签图像快照。</param>
    public static V.IImageSource CopyImage(ImageFrame image)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        return V.VisionImage.CopyFrom(
            new V.ImageInfo(
                image.Width,
                image.Height,
                image.Format == EImagePixelFormat.Gray8 ? V.EPixelLayout.Gray8 : V.EPixelLayout.Bgr24
            ),
            image.CopyPixels()
        );
    }

    /// <summary>将几何转换为显式绑定帧的图层；既有整数轮廓坐标为像素中心，DP.Vision采用像素边缘，因此加0.5。</summary>
    /// <param name = "frameId">与图像一致的帧标识，不得复用过期帧标识。</param>
    /// <param name = "geometry">标签侧不可变几何快照，其轮廓采用像素中心坐标。</param>
    public static V.GeometryOverlay ConvertGeometry(string frameId, CanvasGeometry geometry)
    {
        if (geometry == null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        return new V.GeometryOverlay(frameId, GeometryLayers(geometry));
    }

    /// <summary>转换独立对象集合，保留原始游程、孔洞、空Region及轮廓闭合状态。</summary>
    /// <param name = "geometry">包含独立Region及轮廓对象的标签侧几何快照。</param>
    public static IReadOnlyList<V.CanvasLayer> GeometryLayers(CanvasGeometry geometry)
    {
        if (geometry == null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        var regions = geometry
            .Regions.Select(r => new V.Visual(
                r.Id,
                new V.RegionGeometry(
                    r.Runs.Select(run => new V.RegionRun(run.Row, run.StartColumn, run.EndColumnExclusive))
                ),
                r.FillArgb
            ))
            .ToArray();
        // 空对象仍保存在快照中，渲染器跳过其空显示形状。
        var contours = geometry
            .Contours.Select(c => new V.Visual(
                c.Id,
                new V.ContourGeometry(c.Points.Select(p => new V.PointD(p.X + .5, p.Y + .5)), c.Closed),
                c.StrokeArgb
            ))
            .ToArray();
        return Array.AsReadOnly(
            new[]
            {
                new V.CanvasLayer("regions", V.ELayerKind.Region, regions),
                new V.CanvasLayer("xld", V.ELayerKind.Xld, contours, 1),
            }
        );
    }

    /// <summary>将配方轮廓、独立字符范围和分组证据转换为独立只读显示层，不复制像素或执行算法。</summary>
    /// <param name = "regions">配方ROI集合，仅转换显示轮廓。</param>
    /// <param name = "findings">原始诊断证据集合，不删除重复证据。</param>
    /// <param name = "characters">独立字符证据集合，读取其原图范围。</param>
    /// <param name = "captions">是否生成显示标题，不影响原始几何。</param>
    public static IReadOnlyList<V.CanvasLayer> LabelLayers(
        IEnumerable<InspectionRegion> regions,
        IEnumerable<InspectionFinding> findings,
        IEnumerable<CharacterPatch> characters,
        bool captions = true
    )
    {
        if (regions == null || findings == null || characters == null)
        {
            throw new ArgumentNullException();
        }

        var evidence = findings.ToArray();
        V.Geometry Box(PixelRect r)
        {
            return new V.RectangleGeometry(
                new V.PointD(r.X + r.Width / 2.0, r.Y + r.Height / 2.0),
                r.Width,
                r.Height
            );
        }

        var roi = regions.Select(
            (r, i) =>
                new V.Visual(
                    "roi-" + i,
                    Box(r.Bounds),
                    r.Kind == ERegionKind.Ignore ? V.VisionColors.Gray : V.VisionColors.RoyalBlue,
                    captions && !evidence.Any(f => f.Bounds.HasValue && f.Bounds.Value.Equals(r.Bounds))
                        ? r.Name
                        : ""
                )
        );
        var glyphs = characters.Select(
            (c, i) =>
                new V.Visual(
                    "glyph-" + i,
                    Box(c.Bounds),
                    V.VisionColors.ForestGreen,
                    captions ? c.Character : ""
                )
        );
        var marks = evidence
            .Select((f, i) => new { Finding = f, Index = i + 1 })
            .Where(v => v.Finding.Bounds.HasValue)
            .GroupBy(v => v.Finding.Bounds!.Value)
            .Select(
                (group, i) =>
                    new V.Visual(
                        "finding-" + i,
                        Box(group.Key),
                        group.Any(v => v.Finding.Verdict == EInspectionVerdict.Ng)
                            ? V.VisionColors.Crimson
                            : V.VisionColors.DarkOrange,
                        captions
                            ? string.Join(
                                "/",
                                group.Select(v =>
                                    "F"
                                    + v.Index
                                    + (
                                        v.Finding.Code == "barcode_summary"
                                            ? " " + v.Finding.Verdict.ToString().ToUpperInvariant()
                                            : ""
                                    )
                                )
                            )
                            : ""
                    )
            );
        return new[]
        {
            new V.CanvasLayer("label-roi", V.ELayerKind.Roi, roi, 10),
            new V.CanvasLayer("label-glyph", V.ELayerKind.Annotation, glyphs, 20),
            new V.CanvasLayer("label-findings", V.ELayerKind.Annotation, marks, 30),
        };
    }
}
