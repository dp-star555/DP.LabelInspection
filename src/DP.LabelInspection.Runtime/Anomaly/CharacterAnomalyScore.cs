using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Runtime;

/// <summary>逐字符异常检测中一个字符的结果（原图坐标）。</summary>
public sealed class CharacterAnomalyScore
{
    /// <summary>创建结果。</summary>
    /// <param name = "character">字符身份。</param>
    /// <param name = "tokenIndex">在该行中的序号（从0开始）。</param>
    /// <param name = "bounds">分割单元（原图坐标）。</param>
    /// <param name = "status">compared（已检测）、missing_model（库中没有该字符的模型）、unmeasurable（无法测量行几何）或blocked（模型与输入不匹配）。</param>
    /// <param name = "maximumScore">最大块得分；未检测时为0。</param>
    /// <param name = "threshold">该字符模型的阈值；未检测时为0。</param>
    /// <param name = "findings">异常区域（NG，原图坐标）或阻断原因。</param>
    /// <param name = "inkLoss">缺墨检查的最大值（墨量比例）；未做缺墨检查时为null。</param>
    /// <param name = "inkThreshold">缺墨阈值；未做缺墨检查时为null。</param>
    public CharacterAnomalyScore(
        string character,
        int tokenIndex,
        PixelRect bounds,
        string status,
        double maximumScore,
        double threshold,
        IEnumerable<InspectionFinding> findings,
        double? inkLoss = null,
        double? inkThreshold = null
    )
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        TokenIndex = tokenIndex;
        Bounds = bounds;
        Status = status ?? throw new ArgumentNullException(nameof(status));
        MaximumScore = maximumScore;
        Threshold = threshold;
        Findings = Array.AsReadOnly((findings ?? Array.Empty<InspectionFinding>()).ToArray());
        InkLoss = inkLoss;
        InkThreshold = inkThreshold;
    }

    /// <summary>字符身份。</summary>
    public string Character { get; }

    /// <summary>在该行中的序号。</summary>
    public int TokenIndex { get; }

    /// <summary>分割单元（原图坐标）。</summary>
    public PixelRect Bounds { get; }

    /// <summary>检测状态。</summary>
    public string Status { get; }

    /// <summary>最大块得分。</summary>
    public double MaximumScore { get; }

    /// <summary>阈值。</summary>
    public double Threshold { get; }

    /// <summary>缺墨检查的最大值（墨量比例）；未做缺墨检查时为null。</summary>
    public double? InkLoss { get; }

    /// <summary>缺墨阈值；未做缺墨检查时为null。</summary>
    public double? InkThreshold { get; }

    /// <summary>局部块比较的最大得分相对阈值的倍数。</summary>
    public double PatchRatio => Threshold > 0 ? MaximumScore / Threshold : 0;

    /// <summary>缺墨相对阈值的倍数；未做缺墨检查时为0。</summary>
    public double InkRatio => InkLoss is double ink && InkThreshold is double t && t > 0 ? ink / t : 0;

    /// <summary>两项检查中较大的阈值倍数。</summary>
    public double Ratio => Math.Max(PatchRatio, InkRatio);

    /// <summary>异常区域或阻断原因。</summary>
    public IReadOnlyList<InspectionFinding> Findings { get; }

    /// <summary>已检测且没有异常区域。</summary>
    public bool Passed => Status == "compared" && Findings.All(f => f.Verdict != EInspectionVerdict.Ng);
}
