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
    public CharacterAnomalyScore(
        string character,
        int tokenIndex,
        PixelRect bounds,
        string status,
        double maximumScore,
        double threshold,
        IEnumerable<InspectionFinding> findings
    )
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        TokenIndex = tokenIndex;
        Bounds = bounds;
        Status = status ?? throw new ArgumentNullException(nameof(status));
        MaximumScore = maximumScore;
        Threshold = threshold;
        Findings = Array.AsReadOnly((findings ?? Array.Empty<InspectionFinding>()).ToArray());
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

    /// <summary>最大得分相对阈值的倍数。</summary>
    public double Ratio => Threshold > 0 ? MaximumScore / Threshold : 0;

    /// <summary>异常区域或阻断原因。</summary>
    public IReadOnlyList<InspectionFinding> Findings { get; }

    /// <summary>已检测且没有异常区域。</summary>
    public bool Passed => Status == "compared" && Findings.All(f => f.Verdict != EInspectionVerdict.Ng);
}
