using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using OpenCvBarcodePrintInspector = DP.LabelInspection.Tests.SnapshotBarcodeQuality;
using ZxingBarcodeDecoder = DP.LabelInspection.Tests.BarcodeFixtureReader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZXing;
using ZXing.Common;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>修复预处理读码及QR墨迹扩散的边缘带语义。</summary>
[TestClass]
public sealed class CodeRepairTests
{
    private static byte[] Qr(string text, out int size)
    {
        var encoded = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 330,
                Height = 330,
                Margin = 4,
            },
        }.Write(text);
        size = encoded.Width;
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        return pixels;
    }

    /// <summary>原图直接读出时不标记预处理。</summary>
    [TestMethod]
    public void CleanQrIsReadDirectly()
    {
        var pixels = Qr("WF675907", out int size);
        var frame = new PixelSnapshot(size, size, EImagePixelFormat.Gray8, pixels);
        var symbol = new ZxingBarcodeDecoder()
            .Decode(frame, new PixelRect(0, 0, size, size), default)
            .Single();
        Assert.AreEqual("", symbol.Preprocessing);
    }

    /// <summary>精确原图掩膜只保留左侧码；不修改输入图像或将两个码的搜索外接框当作有效ROI。</summary>
    [TestMethod]
    public void VisionBarcodeReaderHonorsImageRegionMask()
    {
        var left = Qr("MASK-LEFT-001", out int size);
        var right = Qr("MASK-RIGHT-002", out _);
        var pixels = new byte[size * 2 * size];
        for (int y = 0; y < size; y++)
        {
            Buffer.BlockCopy(left, y * size, pixels, y * size * 2, size);
            Buffer.BlockCopy(right, y * size, pixels, y * size * 2 + size, size);
        }

        using var source = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(size * 2, size, DP.Vision.EPixelLayout.Gray8),
            pixels
        );
        var region = new DP.Vision.RegionGeometry(
            Enumerable.Range(0, size).Select(y => new DP.Vision.RegionRun(y, 0, size))
        );
        DP.Vision.Algorithms.IMaskedBarcodeReader reader = new DP.Vision.Zxing.ZxingBarcodeDecoder();
        var result = reader.Read(source, new DP.Vision.Algorithms.PixelBounds(0, 0, size * 2, size), region);
        Assert.AreEqual(DP.Vision.Algorithms.EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual("MASK-LEFT-001", result.Observations.Single().Text);
        var after = new byte[pixels.Length];
        source.CopyTo(0, after, 0, after.Length);
        CollectionAssert.AreEqual(pixels, after);
    }

    /// <summary>掩膜不得脱离原图，也不能默默把空交集当成无缺陷的正常读数。</summary>
    [TestMethod]
    public void VisionBarcodeReaderRejectsInvalidRegionMask()
    {
        using var source = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(32, 32, DP.Vision.EPixelLayout.Gray8),
            Enumerable.Repeat((byte)255, 32 * 32).ToArray()
        );
        DP.Vision.Algorithms.IMaskedBarcodeReader reader = new DP.Vision.Zxing.ZxingBarcodeDecoder();
        var bounds = new DP.Vision.Algorithms.PixelBounds(0, 0, 16, 16);
        Assert.ThrowsExactly<ArgumentException>(() =>
            reader.Read(
                source,
                bounds,
                new DP.Vision.RegionGeometry(new[] { new DP.Vision.RegionRun(31, 20, 21) })
            )
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            reader.Read(
                source,
                bounds,
                new DP.Vision.RegionGeometry(new[] { new DP.Vision.RegionRun(31, 20, 33) })
            )
        );
    }

    /// <summary>精确原图掩膜只允许指定码参与读取，原始图像及另一个码保持不变。</summary>
    [TestMethod]
    public void VisionReaderRestrictsDecodingToMaskedRegion()
    {
        var left = Qr("LEFT-ONLY", out int size);
        var right = Qr("RIGHT-ONLY", out _);
        int width = size * 2 + 20;
        var pixels = Enumerable.Repeat((byte)255, width * size).ToArray();
        for (int y = 0; y < size; y++)
        {
            Buffer.BlockCopy(left, y * size, pixels, y * width, size);
            Buffer.BlockCopy(right, y * size, pixels, y * width + size + 20, size);
        }

        using var image = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(width, size, DP.Vision.EPixelLayout.Gray8),
            pixels
        );
        var mask = new DP.Vision.RegionGeometry(
            Enumerable.Range(0, size).Select(y => new DP.Vision.RegionRun(y, 0, size))
        );
        DP.Vision.Algorithms.IMaskedBarcodeReader reader = new DP.Vision.Zxing.ZxingBarcodeDecoder();
        var result = reader.Read(image, new DP.Vision.Algorithms.PixelBounds(0, 0, width, size), mask);

        Assert.AreEqual(DP.Vision.Algorithms.EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(1, result.Observations.Count);
        Assert.AreEqual("LEFT-ONLY", result.Observations[0].Text);
        var after = new byte[pixels.Length];
        image.CopyTo(0, after, 0, after.Length);
        CollectionAssert.AreEqual(pixels, after);
    }

    /// <summary>旋转定位的局部ROI经矩阵映射为原图掩膜，读码仍在原始像素上完成。</summary>
    [TestMethod]
    public void LocatedReaderUsesRotatedLocalRoiWithoutRotatingPixels()
    {
        var left = Qr("POSE-CODE", out int size);
        var right = Qr("OTHER-CODE", out _);
        int width = size * 2 + 20;
        var pixels = Enumerable.Repeat((byte)255, width * size).ToArray();
        for (int y = 0; y < size; y++)
        {
            Buffer.BlockCopy(left, y * size, pixels, y * width, size);
            Buffer.BlockCopy(right, y * size, pixels, y * width + size + 20, size);
        }
        using var image = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(width, size, DP.Vision.EPixelLayout.Gray8),
            pixels
        );
        using var frame = new DP.Vision.ImageFrame("current-qr", image);
        var coordinates = DP.Vision.Algorithms.VisionCoordinateBuilder.FromPose(
            new DP.Vision.Algorithms.VisionCoordinateDefinition("qr-location", "QR定位", reference: "template-signature"),
            frame,
            new DP.Vision.PointD(width / 2d, size / 2d),
            Math.PI / 2
        );
        var local = coordinates.ToLocalGeometry(
            // 留出1px白色静区，避免旋转往返的浮点尾差被严格边界校验当作越界。
            new DP.Vision.RectangleGeometry(new DP.Vision.PointD(size / 2d, size / 2d), size - 2, size - 2)
        );
        DP.Vision.Algorithms.IMaskedBarcodeReader reader = new DP.Vision.Zxing.ZxingBarcodeDecoder();
        var result = DP.Vision.Algorithms.LocatedBarcodeReading.ReadLocated(
            reader,
            frame,
            coordinates,
            new[] { local },
            Array.Empty<DP.Vision.Geometry>()
        );
        Assert.AreEqual(DP.Vision.Algorithms.EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual("POSE-CODE", result.Observations.Single().Text);
        var foreign = new DP.Vision.Algorithms.VisionCoordinateSystem(
            coordinates.Definition, "other-frame", width, size, coordinates.LocalToImage
        );
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DP.Vision.Algorithms.LocatedBarcodeReading.ReadLocated(
                reader,
                frame,
                foreign,
                new[] { local },
                Array.Empty<DP.Vision.Geometry>()
            )
        );
    }

    /// <summary>空掩膜以及完全位于搜索范围外的掩膜必须显式拒绝，不得回退到整框读码。</summary>
    [TestMethod]
    public void VisionReaderRejectsMasksWithoutSearchPixels()
    {
        var pixels = Qr("MASK-CHECK", out int size);
        using var image = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(size, size, DP.Vision.EPixelLayout.Gray8),
            pixels
        );
        DP.Vision.Algorithms.IMaskedBarcodeReader reader = new DP.Vision.Zxing.ZxingBarcodeDecoder();
        var search = new DP.Vision.Algorithms.PixelBounds(0, 0, size / 2, size);
        Assert.ThrowsExactly<ArgumentException>(() =>
            reader.Read(image, search, new DP.Vision.RegionGeometry(Array.Empty<DP.Vision.RegionRun>()))
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            reader.Read(
                image,
                search,
                new DP.Vision.RegionGeometry(new[] { new DP.Vision.RegionRun(0, size - 1, size) })
            )
        );
    }

    /// <summary>密集斑点使原图读不出；修复后读出的内容正确，并报告可读性余量不足。</summary>
    [TestMethod]
    public void SpeckledQrIsReadAfterRepairAndReported()
    {
        var pixels = Qr("WF675907", out int size);
        var random = new Random(1);
        for (int i = 0; i < pixels.Length; i++)
        {
            if (random.NextDouble() < .1)
            {
                pixels[i] = (byte)(255 - pixels[i]);
            }
        }

        var frame = new PixelSnapshot(size, size, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, size, size);
        var symbol = new ZxingBarcodeDecoder().Decode(frame, bounds, default).Single();
        Assert.AreEqual("WF675907", symbol.Text);
        Assert.AreNotEqual("", symbol.Preprocessing);
        Assert.IsNotNull(symbol.ModuleGrid);
        var findings = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            new[] { symbol },
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(
            findings.Any(f => f.Code == "barcode_decode_assisted" && f.Verdict == EInspectionVerdict.Ng),
            string.Join(";", findings.Select(f => f.Code))
        );
    }

    /// <summary>整体墨迹外扩1像素（热敏打印常见）只触及模块交界边缘带，不是模块缺陷。</summary>
    [TestMethod]
    public void QrInkSpreadIsNotModuleDefect()
    {
        var clean = Qr("WF675907", out int size);
        var spread = (byte[])clean.Clone();
        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                bool neighbourInk = false;
                for (int dy = -1; dy <= 1 && !neighbourInk; dy++)
                {
                    for (int dx = -1; dx <= 1 && !neighbourInk; dx++)
                    {
                        neighbourInk = clean[(y + dy) * size + x + dx] == 0;
                    }
                }

                if (neighbourInk)
                {
                    spread[y * size + x] = 0;
                }
            }
        }

        var frame = new PixelSnapshot(size, size, EImagePixelFormat.Gray8, spread);
        var bounds = new PixelRect(0, 0, size, size);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.IsNotNull(symbols.Single().ModuleGrid);
        var findings = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(),
            default
        );
        Assert.IsTrue(
            findings.Any(f => f.Code == "qr_print_scope"),
            string.Join(";", findings.Select(f => f.Message))
        );
        Assert.IsFalse(
            findings.Any(f => f.Code == "qr_missing_ink" || f.Code == "qr_extra_ink"),
            string.Join(";", findings.Select(f => f.Message))
        );
    }

    /// <summary>预处理标记经中立契约往返不丢失；旧构造保持原图直接读出。</summary>
    [TestMethod]
    public void PreprocessingSurvivesAdapterRoundTrip()
    {
        var bounds = new PixelRect(1, 2, 30, 40);
        var assisted = new BarcodeObservation("X", "QR_CODE", bounds, null, "median5");
        Assert.AreEqual("median5", Bridge.ToLabel(Bridge.ToVision(assisted)).Preprocessing);
        Assert.AreEqual("", new BarcodeObservation("X", "QR_CODE", bounds).Preprocessing);
        Assert.AreEqual("", new BarcodeObservation("X", "QR_CODE", bounds, null, null).Preprocessing);
    }
}
