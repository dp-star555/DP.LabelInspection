using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>局部条纹和空隙缺陷以原图像素测量，与内容解码独立。</summary>
[TestClass]
public sealed class BarcodePrintTests
{
    /// <summary>检查干净的水平和垂直条带，不虚构缺陷。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CleanBars(bool rotated)
    {
        var frame = Bars(false, false, rotated);
        var findings = Inspect(frame);
        Assert.IsFalse(findings.Any(f => f.Verdict == EInspectionVerdict.Ng));
        Assert.IsTrue(findings.Any(f => f.Code == "barcode_print_scope"));
    }

    /// <summary>实际生成的CODE93、CODE128及CODE39码在默认阈值下保持无缺陷。</summary>
    [TestMethod]
    [DataRow(ZXing.BarcodeFormat.CODE_93)]
    [DataRow(ZXing.BarcodeFormat.CODE_128)]
    [DataRow(ZXing.BarcodeFormat.CODE_39)]
    public void CleanEncodedSymbols(ZXing.BarcodeFormat format)
    {
        var writer = new ZXing.BarcodeWriterPixelData
        {
            Format = format,
            Options = new ZXing.Common.EncodingOptions
            {
                Width = 600,
                Height = 140,
                Margin = 16,
            },
        };
        var encoded = writer.Write("WF675907");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var symbols = new DP.LabelInspection.Runtime.Codes.ZxingBarcodeDecoder().Decode(
            frame,
            bounds,
            default
        );
        Assert.AreEqual(1, symbols.Count);
        var findings = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(findings.Any(f => f.Code == "barcode_print_scope"));
        Assert.IsFalse(findings.Any(f => f.Verdict == EInspectionVerdict.Ng));
    }

    /// <summary>即使ROI包含宽静区，局部孔洞和污点仍具有精确范围及面积。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingAndExtraInk(bool rotated)
    {
        var frame = Bars(true, true, rotated);
        var findings = Inspect(frame);
        var missing = findings.Single(f => f.Code == "barcode_missing_ink");
        var extra = findings.Single(f => f.Code == "barcode_extra_ink");
        Assert.AreEqual(24, missing.AreaPixels);
        Assert.AreEqual(24, extra.AreaPixels);
        Assert.AreEqual(
            rotated ? new PixelRect(45, 70, 8, 3) : new PixelRect(70, 45, 3, 8),
            missing.Bounds!.Value
        );
        Assert.AreEqual(
            rotated ? new PixelRect(65, 94, 8, 3) : new PixelRect(94, 65, 3, 8),
            extra.Bounds!.Value
        );
    }

    /// <summary>明确的一维码纯质量检查不要求解码，也不要求未选择的全图采集质量关卡。</summary>
    [TestMethod]
    [DataRow(0.0, EInspectionVerdict.Ng)]
    [DataRow(10000000.0, EInspectionVerdict.Ng)]
    public void IndependentQualityPath(double sharpness, EInspectionVerdict verdict)
    {
        var frame = Bars(true, false, false);
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "bar",
                    frame.Width,
                    frame.Height,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[]
                    {
                        new InspectionRegion(
                            "bar",
                            ERegionKind.Barcode,
                            new PixelRect(0, 0, frame.Width, frame.Height),
                            field: new FieldSettings(barcodeType: EBarcodeKind.OneDimensional)
                        ).WithTasks(new RoiInspectionTasks(false, true)),
                    },
                    new InspectionOptions(minimumSharpness: sharpness)
                )
            )
        );
        Assert.AreEqual(verdict, report.Verdict);
        Assert.AreEqual(
            verdict,
            report.Analysis.Regions[0].Findings.Single(f => f.Code == "barcode_missing_ink").Verdict
        );
    }

    /// <summary>不支持的几何保持未知，关闭印刷项目不额外产生警告。</summary>
    [TestMethod]
    public void UnsupportedAndDisabled()
    {
        var frame = Bars(false, false, false);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var checker = new OpenCvBarcodePrintInspector();
        var qr = new[] { new BarcodeObservation("A", "QR_CODE", bounds) };
        Assert.AreEqual(
            "barcode_print_review",
            checker.Inspect(frame, bounds, qr, new BarcodePrintOptions(), default).Single().Code
        );
        Assert.AreEqual(0, checker.Inspect(frame, bounds, qr, new BarcodePrintOptions(false), default).Count);
        var flat = new ImageFrame(80, 40, EImagePixelFormat.Gray8, new byte[3200]);
        Assert.AreEqual("barcode_print_review", Inspect(flat).Single().Code);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            checker.Inspect(frame, bounds, qr, new BarcodePrintOptions(), new CancellationToken(true))
        );
    }

    /// <summary>阈值按局部定义且可配置，不改变图像。</summary>
    [TestMethod]
    public void AreaAndFractionThresholds()
    {
        var frame = Bars(true, true, false);
        var checker = new OpenCvBarcodePrintInspector();
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        foreach (
            var options in new[]
            {
                new BarcodePrintOptions(minimumArea: 25),
                new BarcodePrintOptions(minimumFraction: .5),
            }
        )
        {
            Assert.IsFalse(
                checker
                    .Inspect(frame, bounds, Array.Empty<BarcodeObservation>(), options, default)
                    .Any(f => f.Verdict == EInspectionVerdict.Ng)
            );
        }
    }

    /// <summary>码下方可读文字不被误当成条纹孔洞。</summary>
    [TestMethod]
    public void HumanReadableLineIsNotBarcodeInk()
    {
        var original = Bars(false, false, false);
        var pixels = Enumerable.Repeat((byte)255, 340 * 185).ToArray();
        Buffer.BlockCopy(original.CopyPixels(), 0, pixels, 0, 340 * 140);
        using var mat = new OpenCvSharp.Mat(185, 340, OpenCvSharp.MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        OpenCvSharp.Cv2.PutText(
            mat,
            "WF675907",
            new OpenCvSharp.Point(20, 155),
            OpenCvSharp.HersheyFonts.HersheySimplex,
            1.1,
            OpenCvSharp.Scalar.All(0),
            2
        );
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        var result = Inspect(new ImageFrame(340, 185, EImagePixelFormat.Gray8, pixels));
        Assert.IsFalse(result.Any(f => f.Verdict == EInspectionVerdict.Ng));
        Assert.IsTrue(result.Any(f => f.Code == "barcode_print_scope"));
    }

    /// <summary>ROI偏移只应用一次，印刷设置经配方序列化后保留。</summary>
    [TestMethod]
    public void OffsetAndRecipePersistence()
    {
        var chip = Bars(true, false, false);
        var source = chip.CopyPixels();
        var pixels = Enumerable.Repeat((byte)255, 500 * 220).ToArray();
        for (int y = 0; y < chip.Height; y++)
        {
            Buffer.BlockCopy(source, y * chip.Width, pixels, (y + 13) * 500 + 17, chip.Width);
        }

        var frame = new ImageFrame(500, 220, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(17, 13, 340, 140);
        var found = new OpenCvBarcodePrintInspector()
            .Inspect(frame, bounds, Array.Empty<BarcodeObservation>(), new BarcodePrintOptions(), default)
            .Single(f => f.Code == "barcode_missing_ink");
        Assert.AreEqual(new PixelRect(87, 58, 3, 8), found.Bounds!.Value);
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dp-print-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var store = new DP.LabelInspection.Storage.InspectionStore(root, new OpenCvImageCodec());
            var recipe = new InspectionRecipe(
                "print",
                500,
                220,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[]
                {
                    new InspectionRegion(
                        "bar",
                        ERegionKind.Barcode,
                        bounds,
                        field: new FieldSettings(
                            barcodePrint: new BarcodePrintOptions(false, 17, .12, 2, true)
                        )
                    ),
                }
            );
            var options = store
                .DeserializeRecipe(store.SerializeRecipe(recipe))
                .Regions[0]
                .Field.BarcodePrint;
            Assert.IsFalse(options.Enabled);
            Assert.AreEqual(17, options.MinimumArea);
            Assert.AreEqual(.12, options.MinimumFraction);
            Assert.AreEqual(2, options.EdgeTolerance);
            Assert.IsTrue(options.CheckQrQuietZone);
        }
        finally
        {
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, true);
            }
        }
    }

    /// <summary>墨迹密度可降低而不跨越Otsu黑白阈值。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GrayInkLossIsNotHiddenByBinarization(bool rotated)
    {
        var original = Bars(false, false, false);
        var pixels = original.CopyPixels();
        for (int y = 45; y < 53; y++)
        {
            for (int x = 70; x < 73; x++)
            {
                pixels[y * original.Width + x] = 90;
            }
        }

        if (rotated)
        {
            var target = new byte[pixels.Length];
            for (int y = 0; y < original.Height; y++)
            {
                for (int x = 0; x < original.Width; x++)
                {
                    target[x * original.Height + y] = pixels[y * original.Width + x];
                }
            }

            pixels = target;
        }

        var findings = Inspect(
            new ImageFrame(
                rotated ? original.Height : original.Width,
                rotated ? original.Width : original.Height,
                EImagePixelFormat.Gray8,
                pixels
            )
        );
        Assert.IsTrue(
            findings.Any(f => f.Code == "barcode_ink_loss" && f.AreaPixels == 24),
            string.Join(";", findings.Select(f => f.Message))
        );
    }

    /// <summary>密度检查可关闭或标定，均匀灰墨不自动视为缺陷。</summary>
    [TestMethod]
    public void InkLossControlsAndUniformInk()
    {
        var original = Bars(false, false, false);
        var pixels = original.CopyPixels();
        for (int y = 45; y < 53; y++)
        {
            for (int x = 70; x < 73; x++)
            {
                pixels[y * original.Width + x] = 90;
            }
        }

        var frame = new ImageFrame(original.Width, original.Height, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var checker = new OpenCvBarcodePrintInspector();
        foreach (
            var options in new[]
            {
                new BarcodePrintOptions(detectInkLoss: false),
                new BarcodePrintOptions(minimumInkLoss: .6),
            }
        )
        {
            Assert.IsFalse(
                checker
                    .Inspect(frame, bounds, Array.Empty<BarcodeObservation>(), options, default)
                    .Any(f => f.Code == "barcode_ink_loss")
            );
        }

        pixels = original.CopyPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == 0)
            {
                pixels[i] = 90;
            }
        }

        Assert.IsFalse(
            Inspect(new ImageFrame(original.Width, original.Height, EImagePixelFormat.Gray8, pixels))
                .Any(f => f.Verdict == EInspectionVerdict.Ng)
        );
    }

    /// <summary>旧设置缺少新字段；字段缺失不同于明确关闭密度检查。</summary>
    [TestMethod]
    public void LegacyInkLossOptionsKeepDefaults()
    {
        var legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<BarcodePrintOptions>(
            "{\"enabled\":true,\"minimumArea\":4,\"minimumFraction\":0.01,\"edgeTolerance\":1}"
        )!;
        Assert.IsTrue(legacy.DetectInkLoss);
        Assert.AreEqual(.25, legacy.MinimumInkLoss);
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<BarcodePrintOptions>(
            Newtonsoft.Json.JsonConvert.SerializeObject(
                new BarcodePrintOptions(detectInkLoss: false, minimumInkLoss: .4)
            )
        )!;
        Assert.IsFalse(restored.DetectInkLoss);
        Assert.AreEqual(.4, restored.MinimumInkLoss);
    }

    private static System.Collections.Generic.IReadOnlyList<InspectionFinding> Inspect(ImageFrame frame)
    {
        return new OpenCvBarcodePrintInspector().Inspect(
            frame,
            new PixelRect(0, 0, frame.Width, frame.Height),
            Array.Empty<BarcodeObservation>(),
            new BarcodePrintOptions(),
            default
        );
    }

    private static ImageFrame Bars(bool missing, bool extra, bool rotated)
    {
        const int w = 340,
            h = 140;
        var pixels = Enumerable.Repeat((byte)255, w * h).ToArray();
        void Rect(int x, int y, int width, int height, byte value)
        {
            for (int row = y; row < y + height; row++)
            {
                for (int col = x; col < x + width; col++)
                {
                    pixels[row * w + col] = value;
                }
            }
        }

        for (int i = 0; i < 16; i++)
        {
            Rect(20 + i * 16, 20, 8, 90, 0);
        }

        if (missing)
        {
            Rect(70, 45, 3, 8, 255);
        }

        if (extra)
        {
            Rect(94, 65, 3, 8, 0);
        }

        if (!rotated)
        {
            return new ImageFrame(w, h, EImagePixelFormat.Gray8, pixels);
        }

        var transposed = new byte[pixels.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                transposed[x * h + y] = pixels[y * w + x];
            }
        }

        return new ImageFrame(h, w, EImagePixelFormat.Gray8, transposed);
    }
}
