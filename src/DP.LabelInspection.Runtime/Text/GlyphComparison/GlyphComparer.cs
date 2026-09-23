using System;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>将现有标签字形契约适配到独立DP.Vision算法。</summary>
public sealed class GlyphComparer : Contracts.IGlyphComparer
{
    private readonly DP.Vision.Algorithms.IGlyphComparer _algorithm;
    internal DP.Vision.Algorithms.IGlyphComparer Algorithm => _algorithm;

    /// <summary>使用已迁移的OpenCV实现，保留现有公开入口。</summary>
    public GlyphComparer()
        : this(new DP.Vision.OpenCv.OpenCvGlyphComparer()) { }

    /// <summary>使用宿主拥有的可替换测量实现，不向其传入字库存储或标签策略。</summary>
    /// <param name = "algorithm">宿主拥有的中立单字比较实现，不由适配器释放。</param>
    public GlyphComparer(DP.Vision.Algorithms.IGlyphComparer algorithm)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <inheritdoc/>
    public GlyphComparison Compare(
        ImageFrame actual,
        GlyphReference reference,
        int threshold = 160,
        int tolerance = 2
    )
    {
        if (actual == null || reference == null)
        {
            throw new ArgumentNullException(nameof(actual));
        }

        using var input = Bridge.ToVision(actual);
        using var expected = Bridge.ToVision(reference.Image);
        using var result = _algorithm.Compare(
            input,
            expected,
            new GlyphComparisonOptions(
                threshold,
                tolerance,
                reference.Binarization == "otsu" ? EGlyphBinarization.Otsu : EGlyphBinarization.Fixed
            )
        );
        if (result.Actual == null || result.Reference == null || result.Delta == null)
        {
            throw new InvalidOperationException(
                "Glyph measurement did not return evidence: " + result.ReasonCode
            );
        }

        return new GlyphComparison(
            result.Status == EAlgorithmStatus.Completed ? "compared" : result.ReasonCode,
            result.Difference,
            result.Missing,
            result.Extra,
            Bridge.ToLabel(result.Actual),
            Bridge.ToLabel(result.Reference),
            Bridge.ToLabel(result.Delta)
        );
    }
}
