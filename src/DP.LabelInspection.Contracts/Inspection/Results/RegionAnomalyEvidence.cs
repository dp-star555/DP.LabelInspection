using System;

namespace DP.LabelInspection.Contracts;

/// <summary>单个ROI局部块异常检测（方法B）的得分证据：所用模型、原图检测范围、最大得分、阈值及热力图。</summary>
public sealed class RegionAnomalyEvidence
{
    /// <summary>创建得分证据。</summary>
    /// <param name = "libraryId">模型库标识。</param>
    /// <param name = "libraryRevision">模型库固定版本。</param>
    /// <param name = "modelKey">所用模型键。</param>
    /// <param name = "modelSha256">所用模型字节的SHA256。</param>
    /// <param name = "featureSource">特征来源。</param>
    /// <param name = "crop">实际参与检测的原图范围。</param>
    /// <param name = "maximumScore">最大块得分。</param>
    /// <param name = "threshold">本次阈值。</param>
    /// <param name = "heatMap">与<paramref name = "crop"/>同尺寸的灰度热力图，128对应阈值；未完成时为null。</param>
    public RegionAnomalyEvidence(
        string libraryId,
        int libraryRevision,
        string modelKey,
        string modelSha256,
        string featureSource,
        PixelRect crop,
        double maximumScore,
        double threshold,
        ImageFrame? heatMap
    )
    {
        LibraryId = libraryId ?? throw new ArgumentNullException(nameof(libraryId));
        LibraryRevision = libraryRevision;
        ModelKey = modelKey ?? throw new ArgumentNullException(nameof(modelKey));
        ModelSha256 = modelSha256 ?? throw new ArgumentNullException(nameof(modelSha256));
        FeatureSource = featureSource ?? throw new ArgumentNullException(nameof(featureSource));
        Crop = crop;
        MaximumScore = maximumScore;
        Threshold = threshold;
        if (heatMap != null && (heatMap.Width != crop.Width || heatMap.Height != crop.Height))
        {
            throw new ArgumentException("Heat map must match the crop.", nameof(heatMap));
        }

        HeatMap = heatMap;
    }

    /// <summary>模型库标识。</summary>
    public string LibraryId { get; }

    /// <summary>模型库固定版本。</summary>
    public int LibraryRevision { get; }

    /// <summary>所用模型键。</summary>
    public string ModelKey { get; }

    /// <summary>所用模型字节的SHA256。</summary>
    public string ModelSha256 { get; }

    /// <summary>特征来源。</summary>
    public string FeatureSource { get; }

    /// <summary>实际参与检测的原图范围。</summary>
    public PixelRect Crop { get; }

    /// <summary>最大块得分。</summary>
    public double MaximumScore { get; }

    /// <summary>本次阈值。</summary>
    public double Threshold { get; }

    /// <summary>最大得分相对阈值的倍数；大于1即存在超阈值块。</summary>
    public double Ratio => Threshold > 0 ? MaximumScore / Threshold : 0;

    /// <summary>灰度热力图（128=阈值），与<see cref = "Crop"/>同尺寸。</summary>
    public ImageFrame? HeatMap { get; }
}
