using System;
using DP.LabelInspection.Contracts;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>测试持久化字形证据的快照转换；生产比较直接使用Vision。</summary>
internal sealed class SnapshotComparer
{
    internal GlyphComparison Compare(PixelSnapshot actual, GlyphReference reference,
        int threshold = 160, int tolerance = 2)
    {
        using var source = Bridge.ToVision(actual);
        using var expected = Bridge.ToVision(reference.Image);
        using var result = new DP.Vision.OpenCv.OpenCvGlyphComparer().Compare(
            source, expected,
            new A.GlyphComparisonOptions(threshold, tolerance,
                Bridge.ToVisionBinarization(reference.Binarization)));
        if (result.Actual == null || result.Reference == null || result.Delta == null)
            throw new InvalidOperationException("Glyph comparison has no pixel evidence.");
        return new GlyphComparison(
            result.Status == A.EAlgorithmStatus.Completed ? "compared" : result.ReasonCode,
            result.Difference, result.Missing, result.Extra,
            Bridge.ToLabel(result.Actual), Bridge.ToLabel(result.Reference), Bridge.ToLabel(result.Delta));
    }
}
