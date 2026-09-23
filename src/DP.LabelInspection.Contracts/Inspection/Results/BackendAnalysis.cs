using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>后台测量结果；Core根据已选项目、执行覆盖及质量证据进行最终判定。</summary>
public sealed class BackendAnalysis
{
    /// <summary>汇总后台测量与区域结果，不用全图统计量代替已选ROI质量检查。</summary>
    /// <param name = "contrast">1%/99%分位灰度差；分阶段流程未要求全图统计时可为NaN。</param>
    /// <param name = "sharpness">拉普拉斯方差；未要求该统计时可为NaN。</param>
    /// <param name = "regions">每个非忽略ROI的结果，集合会复制。</param>
    /// <param name = "offsetX">已应用的参考到实际水平整数平移，单位为原图像素。</param>
    /// <param name = "offsetY">已应用的参考到实际垂直整数平移，单位为原图像素。</param>
    public BackendAnalysis(
        double contrast,
        double sharpness,
        IEnumerable<RegionInspectionResult> regions,
        int offsetX = 0,
        int offsetY = 0
    )
    {
        if (regions == null)
        {
            throw new ArgumentNullException(nameof(regions));
        }

        var copy = regions.ToArray();
        if (copy.Any(r => r == null))
        {
            throw new ArgumentException("Null region result.", nameof(regions));
        }

        OffsetX = offsetX;
        OffsetY = offsetY;
        Contrast = contrast;
        Sharpness = sharpness;
        Regions = new ReadOnlyCollection<RegionInspectionResult>(copy);
    }

    /// <summary>参考到实际的水平像素偏移；报告中的几何已使用实际原图坐标。</summary>
    public int OffsetX { get; }

    /// <summary>参考到实际的垂直像素偏移。</summary>
    public int OffsetY { get; }

    /// <summary>实测灰度对比度，未执行此统计时可为NaN。</summary>
    public double Contrast { get; }

    /// <summary>实测清晰度统计，未执行时可为NaN。</summary>
    public double Sharpness { get; }

    /// <summary>后台提供的区域证据集合。</summary>
    public IReadOnlyList<RegionInspectionResult> Regions { get; }
}
