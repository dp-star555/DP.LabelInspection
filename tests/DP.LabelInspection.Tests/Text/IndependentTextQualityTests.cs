using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

/// <summary>整段文字替代策略使用真实分阶段后台，不强制OCR或伪造字形。</summary>
[TestClass]
public sealed partial class IndependentTextQualityTests
{
    /// <summary>Vision图像租约可直接用于独立文字质检，而不必先转换为旧标签图像。</summary>
    [TestMethod]
    public void VisionFrameRunsIndependentTextQuality()
    {
        var strategy = new Strategy();
        var ink = new DP.Vision.OpenCv.OpenCvInkInspector();
        using var backend = OpenCvInspectionBackend.WithQualityAlgorithms(
            new RegionQualityAlgorithms(ink, ink),
            textQuality: strategy
        );
        using var engine = new InspectionEngine(backend);
        using var pixels = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(32, 32, DP.Vision.EPixelLayout.Gray8),
            new byte[1024]
        );
        using var frame = new DP.Vision.ImageFrame("text-quality", pixels);
        var roi = new InspectionRegion("text", ERegionKind.Text, new PixelRect(0, 0, 32, 32)).WithTasks(
            new RoiInspectionTasks(false, true)
        );
        using var request = InspectionRequest.FromVision(
            frame,
            new InspectionRecipe(
                "quality",
                32,
                32,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            )
        );
        var report = engine.Inspect(request);
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual(ERoiStageState.Passed, report.Analysis.Regions.Single().Execution!.Quality);
        Assert.AreEqual(1, strategy.Calls);
    }

    /// <summary>无参考质量策略可以通过，但必须明确完成。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AlternativeQualityHasNoHiddenDependencies(bool complete)
    {
        var strategy = new Strategy { Complete = complete };
        var ink = new DP.Vision.OpenCv.OpenCvInkInspector();
        using var backend = OpenCvInspectionBackend.WithQualityAlgorithms(
            new RegionQualityAlgorithms(ink, ink),
            textQuality: strategy
        );
        using var engine = new InspectionEngine(backend);
        var frame = new ImageFrame(32, 32, EImagePixelFormat.Gray8, new byte[1024]);
        var roi = new InspectionRegion("text", ERegionKind.Text, new PixelRect(0, 0, 32, 32)).WithTasks(
            new RoiInspectionTasks(false, true)
        );
        var request = new InspectionRequest(
            frame,
            new InspectionRecipe(
                "alternative",
                32,
                32,
                EInspectionMode.Free,
                EAlignmentMode.Translation,
                new[] { roi }
            )
        );
        var report = engine.Inspect(request);
        Assert.AreEqual(1, strategy.Calls);
        Assert.AreEqual(complete ? EInspectionVerdict.Ok : EInspectionVerdict.Ng, report.Verdict);
        var result = report.Analysis.Regions.Single();
        Assert.IsNull(result.Segmentation);
        Assert.IsNull(result.Recognition);
        Assert.AreEqual(ERoiStageState.NotRequested, result.Execution!.Data);
        if (!complete)
        {
            Assert.AreEqual(0, report.EvidenceGroups.Single().LocalizedCandidateCount);
            Assert.IsTrue(result.Findings.Single(f => f.Code == "custom_blocker").IsExecutionBlocker);
        }

    }
}
