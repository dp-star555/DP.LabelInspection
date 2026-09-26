using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 按ROI使用局部块异常检测（PatchCore式，仅良品训练）：从整张良品图裁取ROI训练模型，检测时输出原图坐标的异常区域。
/// 图像须已与配方对齐（固定相机或调用方先做定位）；ROI四周多取少量像素，避免笔画贴边被截断。
/// </summary>
public sealed class RegionAnomalyDetector
{
    private readonly IPatchAnomalyDetector _algorithm;

    /// <summary>使用OpenCV实现。</summary>
    public RegionAnomalyDetector()
        : this(new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector()) { }

    /// <summary>使用宿主提供的中立实现（例如以后的深度特征实现）。</summary>
    /// <param name = "algorithm">宿主拥有的局部块异常检测实现。</param>
    public RegionAnomalyDetector(IPatchAnomalyDetector algorithm)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <summary>
    /// 按ROI内容选择默认参数：固定/空白ROI及带引导值的文字ROI内容固定，用位置相关模式（±3像素），
    /// 缺笔画、笔画内空洞只和良品同一位置比较；可变文字与条码用与位置无关模式。
    /// </summary>
    /// <param name = "region">要检测的ROI。</param>
    public static PatchAnomalyOptions DefaultOptions(InspectionRegion region)
    {
        if (region == null)
        {
            throw new ArgumentNullException(nameof(region));
        }

        bool fixedContent =
            region.Kind == ERegionKind.Fixed
            || region.Kind == ERegionKind.Blank
            || region.Kind == ERegionKind.Text && region.Field.Expected != null;
        return new PatchAnomalyOptions(localRadius: fixedContent ? 3 : (int?)null);
    }

    /// <summary>ROI四周额外裁取的原图像素。</summary>
    public int Margin { get; set; } = 6;

    /// <summary>从若干整张良品图中裁取ROI训练模型。</summary>
    /// <param name = "good">已与配方对齐的整张良品图，至少1张。</param>
    /// <param name = "region">要训练的ROI。</param>
    /// <param name = "options">块大小、记忆库容量与阈值余量。</param>
    /// <param name = "token">协作式取消标记。</param>
    public PatchAnomalyModel Train(
        IReadOnlyList<ImageFrame> good,
        InspectionRegion region,
        PatchAnomalyOptions options,
        CancellationToken token = default
    )
    {
        if (good == null || good.Count == 0 || region == null)
        {
            throw new ArgumentException("Good images and a region are required.");
        }

        var crops = good.Select(g => Bridge.ToVision(g.Crop(Crop(g, region)))).ToList();
        try
        {
            return _algorithm.Train(crops, options, token);
        }
        finally
        {
            foreach (var c in crops)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>检测一张整图上的一个ROI。</summary>
    /// <param name = "image">已与配方对齐的整张待检图。</param>
    /// <param name = "region">要检测的ROI。</param>
    /// <param name = "model">该ROI的模型。</param>
    /// <param name = "options">检测步长、显式阈值与最小面积。</param>
    /// <param name = "token">协作式取消标记。</param>
    public RegionAnomalyResult Inspect(
        ImageFrame image,
        InspectionRegion region,
        PatchAnomalyModel model,
        PatchAnomalyOptions options,
        CancellationToken token = default
    )
    {
        if (image == null || region == null || model == null || options == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        var crop = Crop(image, region);
        using var source = Bridge.ToVision(image.Crop(crop));
        using var result = _algorithm.Detect(source, model, options, token);
        var findings = result
            .Findings.Select(f =>
                f.Bounds is { } b
                    ? new QualityFinding(
                        f.Code,
                        f.Message,
                        f.Kind,
                        new PixelBounds(b.X + crop.X, b.Y + crop.Y, b.Width, b.Height),
                        f.AreaPixels
                    )
                    : f
            )
            .Select(Bridge.ToLabel)
            .ToList();
        return new RegionAnomalyResult(
            region.Name,
            crop,
            result.MaximumScore,
            result.Threshold,
            findings,
            result.HeatMap == null ? null : Bridge.ToLabel(result.HeatMap)
        );
    }

    private PixelRect Crop(ImageFrame image, InspectionRegion region)
    {
        int x0 = Math.Max(0, region.Bounds.X - Margin),
            y0 = Math.Max(0, region.Bounds.Y - Margin),
            x1 = Math.Min(image.Width, region.Bounds.X + region.Bounds.Width + Margin),
            y1 = Math.Min(image.Height, region.Bounds.Y + region.Bounds.Height + Margin);
        if (x1 - x0 < 8 || y1 - y0 < 8)
        {
            throw new ArgumentException("ROI outside image or too small.", nameof(region));
        }

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
