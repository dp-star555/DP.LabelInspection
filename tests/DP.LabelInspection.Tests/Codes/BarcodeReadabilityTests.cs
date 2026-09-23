using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
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
            barcode: new ZxingBarcodeDecoder(),
            barcodePrint: new NeverPrint()
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
            new InspectionRequest(
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

    /// <summary>没有解码器不等于已经执行但解码失败。</summary>
    [TestMethod]
    public void MissingDecoderFailsPrerequisites()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
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
        using var backend = new OpenCvInspectionBackend(barcode: new ZxingBarcodeDecoder());
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
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
        using var backend = new OpenCvInspectionBackend(barcode: new ZxingBarcodeDecoder());
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
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
