using System.Linq;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.LabelInspection.Tests;

/// <summary>按ROI的局部块异常检测：模式按内容选择，异常以原图坐标报告。</summary>
[TestClass]
public sealed class RegionAnomalyTests
{
    private static PixelSnapshot Label(int shift, int seed, bool defect = false)
    {
        using var m = new Mat(80, 220, MatType.CV_8UC1, Scalar.All(235));
        Cv2.Randn(m, Scalar.All(235), Scalar.All(3));
        Cv2.PutText(
            m,
            "AB12",
            new Point(40 + shift, 60),
            HersheyFonts.HersheySimplex,
            1.2,
            Scalar.All(25),
            3
        );
        if (defect)
        {
            // 在“B”竖笔中打一个贯穿的缺口。
            Cv2.Rectangle(m, new Rect(70 + shift, 42, 8, 5), Scalar.All(235), -1);
        }

        var bytes = new byte[m.Rows * m.Cols];
        Marshal.Copy(m.Data, bytes, 0, bytes.Length);
        return new PixelSnapshot(m.Cols, m.Rows, EImagePixelFormat.Gray8, bytes);
    }

    /// <summary>固定内容用位置相关模式，可变文字与条码用与位置无关模式。</summary>
    [TestMethod]
    public void DefaultModeFollowsContent()
    {
        var box = new PixelRect(30, 20, 150, 50);
        Assert.AreEqual(
            3,
            RegionAnomalyDetector
                .DefaultOptions(new InspectionRegion("f", ERegionKind.Fixed, box))
                .LocalRadius
        );
        Assert.AreEqual(
            3,
            RegionAnomalyDetector
                .DefaultOptions(
                    new InspectionRegion(
                        "t",
                        ERegionKind.Text,
                        box,
                        true,
                        new FieldSettings(expected: "AB12")
                    )
                )
                .LocalRadius
        );
        Assert.IsNull(
            RegionAnomalyDetector.DefaultOptions(new InspectionRegion("v", ERegionKind.Text, box)).LocalRadius
        );
        Assert.IsNull(
            RegionAnomalyDetector
                .DefaultOptions(new InspectionRegion("c", ERegionKind.Barcode, box))
                .LocalRadius
        );
    }

    /// <summary>持久化快照转换为Vision输入后，与独立Vision图像产生相同的原图证据。</summary>
    [TestMethod]
    public void VisionAnomalyInspectionMatchesSnapshotEvidence()
    {
        var region = new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(30, 20, 150, 50));
        var detector = new RegionAnomalyDetector();
        var options = RegionAnomalyDetector.DefaultOptions(region);
        var reference = Label(0, 1);
        var model = detector.Train(new[] { reference }, region, options);
        var actual = Label(1, 2);
        using var image = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(actual.Width, actual.Height, DP.Vision.EPixelLayout.Gray8),
            actual.CopyPixels()
        );
        using var snapshotSource = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(actual);
        var old = detector.Inspect(snapshotSource, region, model, options);
        var vision = detector.Inspect(image, region, model, options);
        Assert.AreEqual(old.Crop, vision.Crop);
        Assert.AreEqual(old.MaximumScore, vision.MaximumScore, 1e-9);
        Assert.AreEqual(old.Findings.Count, vision.Findings.Count);
    }

    /// <summary>良品通过；缺口以原图坐标报告，落在ROI内而不是裁图坐标。</summary>
    [TestMethod]
    public void DefectIsReportedInImageCoordinates()
    {
        var region = new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(30, 20, 150, 50));
        var options = RegionAnomalyDetector.DefaultOptions(region);
        var detector = new RegionAnomalyDetector();
        var model = detector.Train(new[] { Label(0, 1), Label(1, 2), Label(0, 3) }, region, options);

        using var cleanSource = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(Label(1, 4));
        var clean = detector.Inspect(cleanSource, region, model, options);
        Assert.IsTrue(clean.Passed, string.Join(";", clean.Findings.Select(f => f.Message)));
        Assert.AreEqual(clean.Crop.Width, clean.HeatMap!.Width);

        using var brokenSource = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(Label(0, 5, defect: true));
        var broken = detector.Inspect(brokenSource, region, model, options);
        var finding = broken.Findings.Single(f => f.Verdict == EInspectionVerdict.Ng);
        var b = finding.Bounds!.Value;
        Assert.IsTrue(b.X <= 78 && b.X + b.Width >= 70 && b.Y <= 47 && b.Y + b.Height >= 42, b.ToString());
        Assert.IsTrue(broken.Ratio > 1);
    }
}
