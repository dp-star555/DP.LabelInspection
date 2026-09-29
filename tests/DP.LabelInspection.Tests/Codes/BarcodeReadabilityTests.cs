using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>明确的可读性要求不同于未执行检查或开放式探索。</summary>
[TestClass]
public sealed partial class BarcodeReadabilityTests
{
    /// <summary>已执行解码但没有结果时为NG，即使采集质量也需要注意。</summary>
    [TestMethod]
    [DataRow(0.0)]
    [DataRow(1000000.0)]
    public void UnreadableQrIsNgWithoutLinearFallback(double sharpness)
    {
        using var backend = new OpenCvInspectionBackend(
            barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder(),
            qrQuality: new NeverPrint()
        );
        using var engine = new InspectionEngine(backend);
        var frame = Blank();
        var region = new InspectionRegion(
            "qr",
            ERegionKind.Barcode,
            new PixelRect(0, 0, 80, 80),
            field: new FieldSettings(barcodeType: EBarcodeKind.QrCode)
        );
        var report = engine.Inspect(
            TestRequests.FromSnapshot(
                frame,
                new InspectionRecipe(
                    "qr",
                    80,
                    80,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { region },
                    new InspectionOptions(minimumContrast: 0, minimumSharpness: sharpness)
                )
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.AreEqual(
            EInspectionVerdict.Ng,
            report.Analysis.Regions.Single().Findings.Single(f => f.Code == "barcode_not_decoded").Verdict
        );
        Assert.AreEqual(ERoiStageState.NotExecuted, report.Analysis.Regions.Single().Execution!.Quality);
        Assert.AreEqual("NG", report.EvidenceGroups.First().Status);
    }

    /// <summary>Vision图像租约入口可以走真实读码流程；释放宿主帧句柄不使请求失效。</summary>
    [TestMethod]
    public void VisionFrameRequestRunsActualBarcodeBackend()
    {
        var pixels = Enumerable.Repeat((byte)255, 80 * 80).ToArray();
        using var source = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(80, 80, DP.Vision.EPixelLayout.Gray8),
            pixels
        );
        var frame = new DP.Vision.ImageFrame("real-barcode-frame", source);
        var roi = new InspectionRegion(
            "qr",
            ERegionKind.Barcode,
            new PixelRect(0, 0, 80, 80),
            field: new FieldSettings(barcodeType: EBarcodeKind.QrCode)
        ).WithTasks(new RoiInspectionTasks(true, false));
        using var request = InspectionRequest.FromVision(
            frame,
            new InspectionRecipe(
                "vision-barcode",
                80,
                80,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            )
        );
        frame.Dispose();
        using var backend = new OpenCvInspectionBackend(
            barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder()
        );
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        Assert.AreEqual("real-barcode-frame", report.Analysis.Alignment!.FrameId);
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsTrue(report.Analysis.Regions.Single().Findings.Any(f => f.Code == "barcode_not_decoded"));
    }

    /// <summary>Vision输入与Vision读码器组合能成功读出QR，并保持原始文本与帧身份。</summary>
    [TestMethod]
    public void VisionReaderDecodesActualQrFromVisionRequest()
    {
        var encoded = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions
            {
                Width = 200,
                Height = 200,
                Margin = 4,
            },
        }.Write("VISION-QR-1020");
        var bytes = new byte[200 * 200];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = encoded.Pixels[i * 4];
        using var pixels = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(200, 200, DP.Vision.EPixelLayout.Gray8),
            bytes
        );
        using var frame = new DP.Vision.ImageFrame("qr-actual", pixels);
        var roi = new InspectionRegion(
            "code",
            ERegionKind.Barcode,
            new PixelRect(0, 0, 200, 200),
            field: new FieldSettings(barcodeType: EBarcodeKind.QrCode)
        ).WithTasks(new RoiInspectionTasks(true, true));
        using var request = InspectionRequest.FromVision(
            frame,
            new InspectionRecipe(
                "vision-qr",
                200,
                200,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            )
        );
        using var backend = new OpenCvInspectionBackend(
            barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder()
        );
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual("VISION-QR-1020", report.Analysis.Regions.Single().Barcodes.Single().Text);
        Assert.AreEqual(ERoiStageState.Passed, report.Analysis.Regions.Single().Execution!.Quality);
        Assert.AreEqual("qr-actual", report.Analysis.Alignment!.FrameId);
    }

    /// <summary>没有解码器不等于已经执行但解码失败。</summary>
    [TestMethod]
    public void MissingDecoderFailsPrerequisites()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            TestRequests.FromSnapshot(
                Blank(),
                new InspectionRecipe(
                    "qr",
                    80,
                    80,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[]
                    {
                        new InspectionRegion(
                            "qr",
                            ERegionKind.Barcode,
                            new PixelRect(0, 0, 80, 80),
                            field: new FieldSettings(barcodeType: EBarcodeKind.QrCode)
                        ),
                    }
                )
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsFalse(
            report.Analysis.Regions.SelectMany(r => r.Findings).Any(f => f.Code == "barcode_not_decoded")
        );
        Assert.IsTrue(
            report.Analysis.Regions.SelectMany(r => r.Findings).Any(f => f.Code == "barcode_unavailable")
        );
    }

    /// <summary>开放式整图搜索没有符号，不能证明预期条码缺失。</summary>
    [TestMethod]
    public void NoConfiguredScopeCannotPass()
    {
        using var backend = new OpenCvInspectionBackend(barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder());
        Assert.IsFalse(backend.Capabilities.HasFlag(EInspectionCapabilities.Discovery));
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            TestRequests.FromSnapshot(
                Blank(),
                new InspectionRecipe(
                    "search",
                    80,
                    80,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    Array.Empty<InspectionRegion>()
                )
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.AreEqual("no_inspection_tasks", report.Findings.Single().Code);
    }

    /// <summary>声明的QR类型经序列化后不变；一维码专用ROI中出现QR属于类型不符。</summary>
    [TestMethod]
    [DataRow(EBarcodeKind.QrCode, false)]
    [DataRow(EBarcodeKind.OneDimensional, true)]
    public void ExplicitFamilyIsCheckedAndPersisted(EBarcodeKind kind, bool mismatch)
    {
        var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldSettings>(
            Newtonsoft.Json.JsonConvert.SerializeObject(
                new FieldSettings(barcodePrint: new BarcodePrintOptions(false), barcodeType: kind)
            )
        )!;
        Assert.AreEqual(kind, field.BarcodeType);
        var encoded = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions
            {
                Width = 200,
                Height = 200,
                Margin = 4,
            },
        }.Write("QR-TEST-A1020");
        var pixels = new byte[200 * 200];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(200, 200, EImagePixelFormat.Gray8, pixels);
        using var backend = new OpenCvInspectionBackend(barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder());
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            TestRequests.FromSnapshot(
                frame,
                new InspectionRecipe(
                    "typed",
                    200,
                    200,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[]
                    {
                        new InspectionRegion(
                            "code",
                            ERegionKind.Barcode,
                            new PixelRect(0, 0, 200, 200),
                            field: field
                        ),
                    },
                    new InspectionOptions(minimumContrast: 0, minimumSharpness: 0)
                )
            )
        );
        Assert.AreEqual(
            mismatch,
            report
                .Analysis.Regions.Single()
                .Findings.Any(f => f.Code == "barcode_type_mismatch" && f.Verdict == EInspectionVerdict.Ng)
        );
        Assert.AreEqual(
            EBarcodeKind.Auto,
            Newtonsoft.Json.JsonConvert.DeserializeObject<FieldSettings>("{}")!.BarcodeType
        );
    }

    private static ImageFrame Blank()
    {
        return new ImageFrame(80, 80, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)255, 6400).ToArray());
    }
}
