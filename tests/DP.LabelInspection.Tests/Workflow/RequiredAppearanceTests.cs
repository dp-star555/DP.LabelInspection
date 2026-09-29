using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>要求的外观检查（绑定字库的质量A）未完整执行或只部分比较时保守判失败，并保留一个无损ROI父项。</summary>
[TestClass]
public sealed partial class RequiredAppearanceTests
{
    private static InspectionReport Run(bool completed, RegionInspectionResult? quality = null)
    {
        using var backend = new Backend(quality, completed);
        using var engine = new InspectionEngine(backend);
        var image = new ImageFrame(32, 32, EImagePixelFormat.Gray8, new byte[1024]);
        var region = new InspectionRegion(
            "text",
            ERegionKind.Text,
            new PixelRect(2, 2, 24, 24),
            true,
            new FieldSettings("font", 2)
        ).WithTasks(new RoiInspectionTasks(true, true));
        return engine.Inspect(
            TestRequests.FromSnapshot(
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

    /// <summary>质量策略报告未完整执行（例如无法分割）时ROI为NG，不能降级为待复核或通过。</summary>
    [TestMethod]
    public void RequestedComparisonWithoutCoverageIsNg()
    {
        var report = Run(
            false,
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
        var findings = report.Analysis.Regions.Single().Findings;
        Assert.IsTrue(findings.Any(f => f.Code == "quality_incomplete" && f.Verdict == EInspectionVerdict.Ng));
        Assert.AreEqual(EInspectionVerdict.Ng, findings.Single(f => f.Code == "segmentation_review").Verdict);
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

    /// <summary>完整执行且全部比较通过时不附加未完成失败。</summary>
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
        Assert.IsFalse(report.Analysis.Regions.Single().Findings.Any(f => f.Code == "quality_incomplete"));
        Assert.AreNotEqual(EInspectionVerdict.Ng, report.Verdict);
    }

    /// <summary>文字ROI只有一个F父项，原子项按顺序保留各自局部标识。</summary>
    [TestMethod]
    public void RoiFindingsHaveOneLosslessParent()
    {
        var bounds = new PixelRect(2, 2, 24, 24);
        var first = new InspectionFinding("missing_template", "1", EInspectionVerdict.Review, bounds);
        var second = new InspectionFinding("missing_template", "2", EInspectionVerdict.Review, bounds);
        var report = Run(true, new RegionInspectionResult("text", new[] { first, second }));
        var group = report.EvidenceGroups.Single(g => g.RegionName == "text");
        var children = group.Children.Where(c => c.Finding.Code == "missing_template").ToArray();
        Assert.AreEqual(2, children.Length);
        Assert.AreEqual(first.Message, children[0].Finding.Message);
        Assert.AreEqual(second.Message, children[1].Finding.Message);
        Assert.IsTrue(group.Children.Select(c => c.Id).All(id => id.StartsWith(group.Id + ".")));
        Assert.AreEqual(group.Children.Count, group.Children.Select(c => c.Id).Distinct().Count());
        Assert.IsFalse(group.IsBarcode);
    }
}
