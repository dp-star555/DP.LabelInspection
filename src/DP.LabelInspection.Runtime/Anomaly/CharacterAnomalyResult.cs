using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Runtime;

/// <summary>一行文字的逐字符异常检测结果。</summary>
public sealed class CharacterAnomalyResult
{
    /// <summary>创建结果。</summary>
    /// <param name = "crop">热力图对应的原图范围（ROI）。</param>
    /// <param name = "scores">逐字符结果，按行内顺序。</param>
    /// <param name = "heatMap">与<paramref name = "crop"/>同尺寸的合成热力图（128对应各字符自己的阈值）；没有已检测字符时为null。</param>
    public CharacterAnomalyResult(
        PixelRect crop,
        IEnumerable<CharacterAnomalyScore> scores,
        ImageFrame? heatMap
    )
    {
        Crop = crop;
        Scores = Array.AsReadOnly((scores ?? throw new ArgumentNullException(nameof(scores))).ToArray());
        HeatMap = heatMap;
    }

    /// <summary>热力图对应的原图范围。</summary>
    public PixelRect Crop { get; }

    /// <summary>逐字符结果。</summary>
    public IReadOnlyList<CharacterAnomalyScore> Scores { get; }

    /// <summary>合成热力图。</summary>
    public ImageFrame? HeatMap { get; }

    /// <summary>已检测字符中最大得分相对各自阈值的倍数。</summary>
    public double WorstRatio =>
        Scores.Where(s => s.Status == "compared").Select(s => s.Ratio).DefaultIfEmpty(0).Max();

    /// <summary>所有字母数字字符都已检测（有模型且可测量）。</summary>
    public bool Completed => Scores.All(s => s.Status == "compared" || s.Status == "missing_model");
}
