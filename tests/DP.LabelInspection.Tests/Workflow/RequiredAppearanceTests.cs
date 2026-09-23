using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>要求但未完成的外观检查保守判失败，并保留一个无损ROI父项。</summary>
[TestClass]
public sealed partial class RequiredAppearanceTests
{
    private static InspectionReport Run(bool bound, params RegionInspectionResult[] results)
    {
        using var backend = new Backend(results);
        using var engine = new InspectionEngine(backend);
        var image = new ImageFrame(32, 32, EImagePixelFormat.Gray8, new byte[1024]);
        var region = new InspectionRegion(
            "text",
            ERegionKind.Text,
            new PixelRect(2, 2, 24, 24),
            true,
            bound ? new FieldSettings("font", 2) : new FieldSettings()
        );
        return engine.Inspect(
            new InspectionRequest(
                image,
                new InspectionRecipe(
                    "test",
                    32,
                    32,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { region }
                )
            )
        );
    }

    /// <summary>即使图像质量评估不可用，分割失败仍为NG。</summary>
    [TestMethod]
    public void RequestedComparisonWithoutCoverageIsNg()
    {
        var report = Run(
            true,
            new RegionInspectionResult(
                "text",
                new[]
                {
                    new InspectionFinding(
                        "segmentation_review",
                        "cannot split",
                        EInspectionVerdict.Review,
                        new PixelRect(2, 2, 24, 24)
                    ),
                }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsTrue(
            report
                .Analysis.Regions.Single()
                .Findings.Any(f => f.Code == "appearance_incomplete" && f.Verdict == EInspectionVerdict.Ng)
        );
    }

    /// <summary>遗漏的已要求ROI不能被误当成可选覆盖。</summary>
    [TestMethod]
    public void MissingBackendRoiIsNg()
    {
        var report = Run(true);
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.AreEqual(1, report.EvidenceGroups.Count(g => g.RegionName == "text"));
    }

    /// <summary>普通未绑定OCR不能静默转为要求的字形检查。</summary>
    [TestMethod]
    public void UnboundTextDoesNotGainAppearanceFailure()
    {
        var report = Run(
            false,
            new RegionInspectionResult(
                "text",
                new[] { new InspectionFinding("ocr_unavailable", "missing", EInspectionVerdict.Review) }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Review, report.Verdict);
        Assert.IsFalse(report.Analysis.Regions.Single().Findings.Any(f => f.Code == "appearance_incomplete"));
    }

    /// <summary>多个有效比较中即使只缺一个参考，要求的ROI仍失败。</summary>
    [TestMethod]
    public void PartialComparisonFailsClosed()
    {
        var image = new ImageFrame(8, 8, EImagePixelFormat.Gray8, new byte[64]);
        var a = new CharacterPatch("A", 0, new PixelRect(2, 2, 8, 8), image);
        var b = new CharacterPatch("B", 1, new PixelRect(12, 2, 8, 8), image);
        var comparison = new GlyphComparison("compared", 0, 0, 0, image, image, image);
        var report = Run(
            true,
            new RegionInspectionResult(
                "text",
                new[] { new InspectionFinding("missing_template", "B", EInspectionVerdict.Review, b.Bounds) },
                segmentation: new CharacterSegmentation("provisional", "test", "test", 2, new[] { a, b }),
                glyphs: new[]
                {
                    new GlyphInspection(a, "compared", "hash", comparison),
                    new GlyphInspection(b, "missing_template"),
                }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.AreEqual(
            EInspectionVerdict.Ng,
            report.Analysis.Regions.Single().Findings.Single(f => f.Code == "missing_template").Verdict
        );
        Assert.AreEqual(0, report.EvidenceGroups.Single(g => g.RegionName == "text").LocalizedCandidateCount);
    }

    /// <summary>仅因质量不确定，不能把已完成覆盖误报为未完成。</summary>
    [TestMethod]
    public void CompletedComparisonDoesNotGainCompletionNg()
    {
        var image = new ImageFrame(8, 8, EImagePixelFormat.Gray8, new byte[64]);
        var a = new CharacterPatch("A", 0, new PixelRect(2, 2, 8, 8), image);
        var comparison = new GlyphComparison("compared", 0, 0, 0, image, image, image);
        var report = Run(
            true,
            new RegionInspectionResult(
                "text",
                Array.Empty<InspectionFinding>(),
                segmentation: new CharacterSegmentation("provisional", "test", "test", 1, new[] { a }),
                glyphs: new[] { new GlyphInspection(a, "compared", "hash", comparison) }
            )
        );
        Assert.IsFalse(report.Analysis.Regions.Single().Findings.Any(f => f.Code == "appearance_incomplete"));
    }

    /// <summary>文字ROI只有一个F父项，原子项均保留各自局部标识。</summary>
    [TestMethod]
    public void RoiFindingsHaveOneLosslessParent()
    {
        var bounds = new PixelRect(2, 2, 24, 24);
        var first = new InspectionFinding("missing_template", "1", EInspectionVerdict.Review, bounds);
        var second = new InspectionFinding("missing_template", "2", EInspectionVerdict.Review, bounds);
        var report = Run(false, new RegionInspectionResult("text", new[] { first, second }));
        var group = report.EvidenceGroups.Single(g => g.RegionName == "text");
        Assert.AreEqual(2, group.Children.Count);
        Assert.AreEqual(first.Message, group.Children[0].Finding.Message);
        Assert.AreEqual(group.Id + ".2", group.Children[1].Id);
        Assert.IsFalse(group.IsBarcode);
    }
}
