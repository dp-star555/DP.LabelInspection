using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Runtime;

/// <summary>单个ROI的局部块异常检测结果（原图坐标）。</summary>
public sealed class RegionAnomalyResult
{
    /// <summary>创建结果快照。</summary>
    /// <param name = "regionName">ROI名称。</param>
    /// <param name = "crop">实际参与检测的原图范围。</param>
    /// <param name = "maximumScore">最大块得分。</param>
    /// <param name = "threshold">本次阈值。</param>
    /// <param name = "findings">异常区域（NG，原图坐标）与说明（OK），内部复制。</param>
    /// <param name = "heatMap">与<paramref name = "crop"/>同尺寸的灰度热力图，128对应阈值。</param>
    public RegionAnomalyResult(
        string regionName,
        PixelRect crop,
        double maximumScore,
        double threshold,
        IEnumerable<InspectionFinding> findings,
        ImageFrame? heatMap
    )
    {
        RegionName = regionName ?? throw new ArgumentNullException(nameof(regionName));
        Crop = crop;
        MaximumScore = maximumScore;
        Threshold = threshold;
        Findings = Array.AsReadOnly((findings ?? Array.Empty<InspectionFinding>()).ToArray());
        HeatMap = heatMap;
    }

    /// <summary>ROI名称。</summary>
    public string RegionName { get; }

    /// <summary>实际参与检测的原图范围。</summary>
    public PixelRect Crop { get; }

    /// <summary>最大块得分。</summary>
    public double MaximumScore { get; }

    /// <summary>本次阈值。</summary>
    public double Threshold { get; }

    /// <summary>最大得分相对阈值的倍数；大于1即存在超阈值块。</summary>
    public double Ratio => Threshold > 0 ? MaximumScore / Threshold : 0;

    /// <summary>异常区域与说明。</summary>
    public IReadOnlyList<InspectionFinding> Findings { get; }

    /// <summary>灰度热力图（128=阈值），与<see cref = "Crop"/>同尺寸。</summary>
    public ImageFrame? HeatMap { get; }

    /// <summary>没有异常区域时为true。</summary>
    public bool Passed => Findings.All(f => f.Verdict != EInspectionVerdict.Ng);
}
