using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.Vision;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BarcodeObservation = DP.LabelInspection.Contracts.BarcodeObservation;

namespace DP.LabelInspection.Tests;

/// <summary>验证真实标签调用经过独立算法接口，且不替换业务证据。</summary>
[TestClass]
public sealed partial class AlgorithmIsolationTests
{
    private static PixelSnapshot White()
    {
        return new PixelSnapshot(32, 16, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)255, 512).ToArray());
    }

    /// <summary>两项ROI算法均由注入提供；首个ROI的局部NG不阻止下一个ROI。</summary>
    [TestMethod]
    public void IndependentFixedAndBlankImplementationsAreUsedForEveryRoi()
    {
        var algorithm = new InkProbe();
        using var backend = OpenCvInspectionBackend.WithQualityAlgorithms(
            new RegionQualityAlgorithms(algorithm, algorithm)
        );
        var image = White();
        var recipe = new InspectionRecipe(
            "seams",
            32,
            16,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[]
            {
                new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(0, 0, 16, 16)),
                new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(16, 0, 16, 16)),
            }
        );
        using var engine = new InspectionEngine(backend);
        var analysis = engine.Inspect(TestRequests.FromSnapshot(image, recipe, image)).Analysis;
        Assert.AreEqual(1, algorithm.FixedCalls);
        Assert.AreEqual(1, algorithm.BlankCalls);
        Assert.AreEqual(2, analysis.Regions.Count);
        Assert.AreEqual(EInspectionVerdict.Ng, analysis.Regions[0].Findings.Single().Verdict);
        Assert.AreEqual(18, analysis.Regions[1].Findings.Single().Bounds!.Value.X);
        Assert.ThrowsExactly<ObjectDisposedException>(() => algorithm.LastInput!.Retain());
    }

    /// <summary>小数坐标测量不能在既有整数报告中静默改变坐标。</summary>
    [TestMethod]
    public void AdapterRejectsNonintegralMeasurementBounds()
    {
        var algorithm = new InkProbe { Fractional = true };
        using var backend = OpenCvInspectionBackend.WithQualityAlgorithms(
            new RegionQualityAlgorithms(algorithm, algorithm)
        );
        var image = White();
        var recipe = new InspectionRecipe(
            "fractional",
            32,
            16,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(0, 0, 16, 16)) }
        );
        using var engine = new InspectionEngine(backend);
        var result = engine.Inspect(TestRequests.FromSnapshot(image, recipe)).Analysis.Regions.Single();
        Assert.AreEqual(ERoiStageState.Failed, result.Execution!.Quality);
        Assert.IsTrue(
            result.Findings.Any(f =>
                f.Code == "roi_execution_failed"
                && f.Verdict == EInspectionVerdict.Ng
                && f.Message.Contains("nonintegral")
            )
        );
    }

    /// <summary>现有构造入口仍可接收null识别器，不产生源代码重载歧义。</summary>
    [TestMethod]
    public void LegacyNullRecognizerConstructorRemainsUsable()
    {
        using var backend = new OpenCvInspectionBackend(null);
        Assert.IsNotNull(backend);
    }

    /// <summary>QR质量实现可借用替换，不在Inspect内部创建。</summary>
    [TestMethod]
    public void QrQualityIsIndependentlyInjected()
    {
        var qr = new QrProbe();
        using var backend = new OpenCvInspectionBackend(qrQuality: qr);
        using var engine = new DP.LabelInspection.Core.InspectionEngine(backend);
        using var image = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(White());
        using var frame = new DP.Vision.ImageFrame("qr-quality-probe", image);
        using var request = InspectionRequest.FromVision(frame,
            new InspectionRecipe("qr-quality", 32, 16, EInspectionMode.Free,
                EAlignmentMode.AssumeAligned, new[] {
                    new InspectionRegion("qr", ERegionKind.Barcode, new PixelRect(0, 0, 32, 16),
                        field: new FieldSettings(barcodeType: EBarcodeKind.QrCode))
                        .WithTasks(new RoiInspectionTasks(false, true))
                }));
        var result = engine.Inspect(request);
        Assert.AreEqual(1, qr.Calls);
        Assert.IsTrue(result.Analysis.Regions.Single().Findings.Any(f => f.Code == "replacement_qr"));
    }

    /// <summary>Vision比较器显式拥有证据租约，释放后不能再借用。</summary>
    [TestMethod]
    public void NeutralComparerEvidenceHasExplicitLifetime()
    {
        var algorithm = new GlyphProbe();
        var frame = White().Crop(new PixelRect(0, 0, 16, 16));
        using var source = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(frame);
        using var reference = source.Retain();
        var result = algorithm.Compare(source, reference,
            new GlyphComparisonOptions(147, 0, EGlyphBinarization.Fixed));
        Assert.AreEqual(1, algorithm.Calls);
        Assert.AreEqual(147, algorithm.Options!.Threshold);
        Assert.AreEqual(EGlyphBinarization.Fixed, algorithm.Options.Binarization);
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(.125, result.Difference);
        Assert.AreEqual(256, result.Delta!.Info.ByteLength);
        result.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => algorithm.LastResult!.Actual!.Retain());
    }
}
