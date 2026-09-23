using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

/// <summary>真实QR检测、解码及模块内部印刷回归。</summary>
[TestClass]
public sealed class QrPrintTests
{
    /// <summary>验证两个方向的干净QR及局部黑/白模块损伤。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void QrModuleDefects(bool defects, bool rotated)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 400,
                Height = 400,
                Margin = 4,
            },
        };
        var encoded = writer.Write("WF675907");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var decoder = new ZxingBarcodeDecoder();
        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var initial = decoder.Decode(frame, bounds, default).Single();
        Assert.IsNotNull(initial.ModuleGrid);
        var grid = initial.ModuleGrid!;
        var expected = new System.Collections.Generic.List<PixelRect>();
        if (defects)
        {
            double size = (grid.Corners[2] - grid.Corners[0]) / grid.Dimension;
            foreach (bool black in new[] { true, false })
            {
                int cell = Enumerable
                    .Range(0, grid.Dimension * grid.Dimension)
                    .First(i =>
                        i / grid.Dimension >= 10
                        && i % grid.Dimension >= 10
                        && grid.SampledModules[i] == black
                    );
                int x = (int)Math.Ceiling(grid.Corners[0] + (cell % grid.Dimension + .25) * size),
                    y = (int)Math.Ceiling(grid.Corners[1] + (cell / grid.Dimension + .25) * size);
                expected.Add(
                    rotated ? new PixelRect(encoded.Height - y - 2, x, 2, 2) : new PixelRect(x, y, 2, 2)
                );
                for (int row = y; row < y + 2; row++)
                {
                    for (int col = x; col < x + 2; col++)
                    {
                        pixels[row * encoded.Width + col] = black ? (byte)255 : (byte)0;
                    }
                }
            }
        }

        if (rotated)
        {
            var target = new byte[pixels.Length];
            for (int y = 0; y < encoded.Height; y++)
            {
                for (int x = 0; x < encoded.Width; x++)
                {
                    target[x * encoded.Height + (encoded.Height - 1 - y)] = pixels[y * encoded.Width + x];
                }
            }

            pixels = target;
        }

        frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var decoded = decoder.Decode(frame, bounds, default);
        Assert.AreEqual("WF675907", decoded.Single().Text);
        Assert.IsNotNull(decoded.Single().ModuleGrid);
        var findings = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            decoded,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(
            findings.Any(f => f.Code == "qr_print_scope"),
            string.Join(";", findings.Select(f => f.Message))
        );
        if (defects)
        {
            Assert.AreEqual(4, findings.Single(f => f.Code == "qr_missing_ink").AreaPixels);
            Assert.AreEqual(4, findings.Single(f => f.Code == "qr_extra_ink").AreaPixels);
            Assert.AreEqual(expected[0], findings.Single(f => f.Code == "qr_missing_ink").Bounds!.Value);
            Assert.AreEqual(expected[1], findings.Single(f => f.Code == "qr_extra_ink").Bounds!.Value);
        }
        else
        {
            Assert.IsFalse(
                findings.Any(f => f.Verdict != EInspectionVerdict.Ok),
                string.Join(";", findings.Select(f => f.Message))
            );
        }
    }

    /// <summary>检查带校正图形的版本；不可靠透视几何保持待复核，不虚构缺陷。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LargerVersionAndPerspective(bool perspective)
    {
        string text = string.Concat(Enumerable.Repeat("WF675907-", 15));
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 600,
                Height = 600,
                Margin = 4,
            },
        };
        var encoded = writer.Write(text);
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        using var mat = new OpenCvSharp.Mat(600, 600, OpenCvSharp.MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        using var warped = new OpenCvSharp.Mat();
        if (perspective)
        {
            using var transform = OpenCvSharp.Cv2.GetPerspectiveTransform(
                new[]
                {
                    new OpenCvSharp.Point2f(0, 0),
                    new OpenCvSharp.Point2f(600, 0),
                    new OpenCvSharp.Point2f(600, 600),
                    new OpenCvSharp.Point2f(0, 600),
                },
                new[]
                {
                    new OpenCvSharp.Point2f(45, 20),
                    new OpenCvSharp.Point2f(610, 45),
                    new OpenCvSharp.Point2f(640, 610),
                    new OpenCvSharp.Point2f(10, 640),
                }
            );
            OpenCvSharp.Cv2.WarpPerspective(
                mat,
                warped,
                transform,
                new OpenCvSharp.Size(660, 660),
                OpenCvSharp.InterpolationFlags.Nearest,
                OpenCvSharp.BorderTypes.Constant,
                OpenCvSharp.Scalar.All(255)
            );
        }
        else
        {
            mat.CopyTo(warped);
        }

        var data = new byte[warped.Width * warped.Height];
        System.Runtime.InteropServices.Marshal.Copy(warped.Data, data, 0, data.Length);
        var frame = new ImageFrame(warped.Width, warped.Height, EImagePixelFormat.Gray8, data);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual(text, symbols.Single().Text);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        if (perspective && symbols.Single().ModuleGrid == null)
        {
            Assert.IsTrue(result.Any(f => f.Code == "barcode_print_review"));
            Assert.IsFalse(result.Any(f => f.Verdict == EInspectionVerdict.Ng));
            return;
        }

        Assert.IsTrue(symbols.Single().ModuleGrid!.Dimension > 21);
        Assert.IsTrue(
            result.Any(f => f.Code == "qr_print_scope"),
            string.Join(";", result.Select(f => f.Message))
        );
        Assert.IsFalse(
            result.Any(f => f.Verdict != EInspectionVerdict.Ok),
            string.Join(";", result.Select(f => f.Message))
        );
    }

    /// <summary>完整时序模块按QR规则检查，不依据受损后的多数颜色。</summary>
    [TestMethod]
    public void WholeFunctionalModulesAreDetected()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 400,
                Height = 400,
                Margin = 4,
            },
        };
        var encoded = writer.Write("QR-TEST-A1020");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var bounds = new PixelRect(0, 0, encoded.Width, encoded.Height);
        var decoder = new ZxingBarcodeDecoder();
        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var grid = decoder.Decode(frame, bounds, default).Single().ModuleGrid!;
        double scale = (grid.Corners[2] - grid.Corners[0]) / grid.Dimension;
        foreach (int col in new[] { 10, 11 })
        {
            int left = (int)Math.Ceiling(grid.Corners[0] + col * scale),
                right = (int)Math.Ceiling(grid.Corners[0] + (col + 1) * scale),
                top = (int)Math.Ceiling(grid.Corners[1] + 6 * scale),
                bottom = (int)Math.Ceiling(grid.Corners[1] + 7 * scale);
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    pixels[y * encoded.Width + x] = col == 10 ? (byte)255 : (byte)0;
                }
            }
        }

        frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var symbols = decoder.Decode(frame, bounds, default);
        Assert.AreEqual("QR-TEST-A1020", symbols.Single().Text);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(
            result.Any(f => f.Code == "qr_missing_ink" && f.Message.Contains("固定结构规则参考")),
            string.Join(";", result.Select(f => f.Message))
        );
        Assert.IsTrue(result.Any(f => f.Code == "qr_extra_ink" && f.Message.Contains("固定结构规则参考")));
    }

    /// <summary>校正中心规则与生成符号一致，包含版本32的特殊间距。</summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(32)]
    [DataRow(40)]
    public void FunctionalPatternsMatchQrVersions(int version)
    {
        int side = (17 + 4 * version + 8) * 6;
        var options = new EncodingOptions
        {
            Width = side,
            Height = side,
            Margin = 4,
        };
        options.Hints[EncodeHintType.QR_VERSION] = version;
        var encoded = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = options }.Write(
            "QR-TEST-A1020"
        );
        var pixels = new byte[side * side];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(side, side, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, side, side);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual(17 + 4 * version, symbols.Single().ModuleGrid!.Dimension);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(result.Any(f => f.Code == "qr_print_scope"));
        Assert.IsFalse(
            result.Any(f => f.Verdict != EInspectionVerdict.Ok),
            string.Join(";", result.Select(f => f.Message))
        );
    }

    /// <summary>镜像解码不能对未纠正网格直接应用标准功能规则。</summary>
    [TestMethod]
    public void MirroredSymbolIsNotGivenFalseStructuralDefects()
    {
        var encoded = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 400,
                Height = 400,
                Margin = 4,
            },
        }.Write("QR-TEST-A1020");
        var pixels = new byte[400 * 400];
        for (int y = 0; y < 400; y++)
        {
            for (int x = 0; x < 400; x++)
            {
                pixels[x * 400 + y] = encoded.Pixels[(y * 400 + x) * 4];
            }
        }

        var frame = new ImageFrame(400, 400, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, 400, 400);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual("QR-TEST-A1020", symbols.Single().Text);
        Assert.IsNull(symbols.Single().ModuleGrid);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(result.Any(f => f.Code == "barcode_print_review"));
        Assert.IsFalse(result.Any(f => f.Verdict == EInspectionVerdict.Ng));
    }

    /// <summary>低分辨率网格不能静默通过模块检查。</summary>
    [TestMethod]
    public void SmallModulesRequireReview()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 58,
                Height = 58,
                Margin = 4,
            },
        };
        var encoded = writer.Write("A");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual(1, symbols.Count);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(result.Any(f => f.Code == "barcode_print_review"));
        Assert.IsFalse(result.Any(f => f.Code == "qr_print_scope"));
    }
}
