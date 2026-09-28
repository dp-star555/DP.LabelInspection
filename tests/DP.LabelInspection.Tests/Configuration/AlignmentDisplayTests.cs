using System;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>回归旧平移叠加的安全边界。</summary>
[TestClass]
public sealed class AlignmentDisplayTests
{
    /// <summary>不能把旋转或与旧偏移不一致的矩阵当作平移。</summary>
    [TestMethod]
    public void TranslationOnlyRequiresMatrixToMatchLegacyOffsets()
    {
        var translation = new BackendAnalysis(0, 0, Array.Empty<RegionInspectionResult>(), 3, -2,
            new InspectionAlignment("frame", CoordinateMatrix2D.FromAffine(1, 0, 3, 0, 1, -2), true));
        Assert.IsTrue(translation.TryGetTranslation(out int x, out int y));
        Assert.AreEqual(3, x);
        Assert.AreEqual(-2, y);

        var rotation = new BackendAnalysis(0, 0, Array.Empty<RegionInspectionResult>(), 3, -2,
            new InspectionAlignment("frame", CoordinateMatrix2D.FromAffine(0, -1, 3, 1, 0, -2), true));
        Assert.IsFalse(rotation.TryGetTranslation(out _, out _));
        var inconsistent = new BackendAnalysis(0, 0, Array.Empty<RegionInspectionResult>(), 3, -2,
            new InspectionAlignment("frame", CoordinateMatrix2D.Identity, false));
        Assert.IsFalse(inconsistent.TryGetTranslation(out _, out _));
    }

    /// <summary>无矩阵的历史报告仍能使用原来的偏移。</summary>
    [TestMethod]
    public void HistoricalReportsKeepTheirOffsets()
    {
        var historical = new BackendAnalysis(0, 0, Array.Empty<RegionInspectionResult>(), 3, -2);
        Assert.IsTrue(historical.TryGetTranslation(out int x, out int y));
        Assert.AreEqual(3, x);
        Assert.AreEqual(-2, y);
    }
}
