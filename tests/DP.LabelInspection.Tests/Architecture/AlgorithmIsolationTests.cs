using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.Vision;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BarcodeObservation = DP.LabelInspection.Contracts.BarcodeObservation;
using BarcodePrintOptions = DP.LabelInspection.Contracts.BarcodePrintOptions;
using ImageFrame = DP.LabelInspection.Contracts.ImageFrame;

namespace DP.LabelInspection.Tests;

/// <summary>验证真实标签调用经过独立算法接口，且不替换业务证据。</summary>
[TestClass]
public sealed partial class AlgorithmIsolationTests
{
    private static ImageFrame White()
    {
        return new ImageFrame(32, 16, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)255, 512).ToArray());
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
        var analysis = backend.Analyze(new InspectionRequest(image, recipe, image), CancellationToken.None);
        Assert.AreEqual(1, algorithm.FixedCalls);
        Assert.AreEqual(1, algorithm.BlankCalls);
        Assert.AreEqual(2, analysis.Regions.Count);
        Assert.AreEqual(EInspectionVerdict.Ng, analysis.Regions[0].Findings.Single().Verdict);
        Assert.AreEqual(18, analysis.Regions[1].Findings.Single().Bounds!.Value.X);
        Assert.ThrowsExactly<ObjectDisposedException>(() => algorithm.LastInput!.Retain());
    }

    /// <summary>小数坐标测量不能在既有整数报告中静默改变坐标。</summary>
    [TestMethod]
    public void LegacyAdapterRejectsNonintegralMeasurementBounds()
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
        var result = backend
            .Analyze(new InspectionRequest(image, recipe), CancellationToken.None)
            .Regions.Single();
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
        var router = new OpenCvBarcodePrintInspector(qr);
        var image = White();
        var bounds = new PixelRect(0, 0, 32, 16);
        var observations = new[] { new BarcodeObservation("payload", "QR_CODE", bounds) };
        var result = router.Inspect(
            image,
            bounds,
            observations,
            new BarcodePrintOptions(),
            CancellationToken.None
        );
        Assert.AreEqual(1, qr.Calls);
        Assert.AreEqual("replacement_qr", result.Single().Code);
        router.Inspect(
            image,
            bounds,
            observations,
            new BarcodePrintOptions(enabled: false),
            CancellationToken.None
        );
        Assert.AreEqual(1, qr.Calls);
        router.Inspect(
            image,
            bounds,
            new[] { new BarcodeObservation("payload", "CODE_128", bounds) },
            new BarcodePrintOptions(),
            CancellationToken.None
        );
        Assert.AreEqual(1, qr.Calls);
    }

    /// <summary>标签比较器委托给中立实现，并在释放租约前复制证据。</summary>
    [TestMethod]
    public void GlyphCompatibilityAdapterUsesInjectedNeutralComparer()
    {
        var algorithm = new GlyphProbe();
        var legacy = new GlyphComparer(algorithm);
        var frame = White().Crop(new PixelRect(0, 0, 16, 16));
        var result = legacy.Compare(frame, new GlyphReference("A", frame, "pinned", "fixed"), 147, 0);
        Assert.AreEqual(1, algorithm.Calls);
        Assert.AreEqual(147, algorithm.Options!.Threshold);
        Assert.AreEqual(EGlyphBinarization.Fixed, algorithm.Options.Binarization);
        Assert.AreEqual("compared", result.Status);
        Assert.AreEqual(.125, result.Difference);
        Assert.AreEqual(256, result.Delta.CopyPixels().Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => algorithm.LastResult!.Actual!.Retain());
    }
}
