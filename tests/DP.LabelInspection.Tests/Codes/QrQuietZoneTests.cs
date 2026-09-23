using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

/// <summary>显式启用的QR静区原图像素证据测试。</summary>
[TestClass]
public sealed class QrQuietZoneTests
{
    /// <summary>启用且干净、启用且有缺陷、禁用且有缺陷三种情形结果独立。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void QuietZoneIsIndependent(bool enabled, bool dirty)
    {
        var frame = Image(dirty);
        var bounds = new PixelRect(0, 0, 400, 400);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual(1, symbols.Count);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(checkQrQuietZone: enabled),
            default
        );
        Assert.IsTrue(result.Any(f => f.Code == "qr_print_scope"));
        Assert.AreEqual(enabled, result.Any(f => f.Code == "qr_quiet_zone_scope"));
        if (enabled && dirty)
        {
            var defect = result.Single(f => f.Code == "qr_quiet_zone_ink");
            Assert.AreEqual(9, defect.AreaPixels);
            Assert.AreEqual(new PixelRect(40, 40, 3, 3), defect.Bounds!.Value);
        }
        else
        {
            Assert.IsFalse(
                result.Any(f => f.Verdict != EInspectionVerdict.Ok),
                string.Join(";", result.Select(f => f.Message))
            );
        }
    }

    /// <summary>紧裁ROI不能被当作干净静区，同时遵守面积阈值。</summary>
    [TestMethod]
    public void TightRoiAndAreaThreshold()
    {
        var frame = Image(true);
        var full = new PixelRect(0, 0, 400, 400);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, full, default);
        var checker = new OpenCvBarcodePrintInspector();
        var tight = checker.Inspect(
            frame,
            new PixelRect(60, 60, 280, 280),
            symbols,
            new BarcodePrintOptions(checkQrQuietZone: true),
            default
        );
        Assert.IsTrue(tight.Any(f => f.Code == "qr_quiet_zone_review"));
        Assert.IsFalse(tight.Any(f => f.Code == "qr_quiet_zone_scope"));
        var tolerant = checker.Inspect(
            frame,
            full,
            symbols,
            new BarcodePrintOptions(minimumArea: 10, checkQrQuietZone: true),
            default
        );
        Assert.IsTrue(tolerant.Any(f => f.Code == "qr_quiet_zone_scope"));
        Assert.IsFalse(tolerant.Any(f => f.Code == "qr_quiet_zone_ink"));
    }

    /// <summary>恰好生成四模块边距即足够，不需要额外图像白边。</summary>
    [TestMethod]
    public void ExactFourModuleMargin()
    {
        const int side = 174;
        var encoded = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = side,
                Height = side,
                Margin = 4,
            },
        }.Write("QR-TEST-A1020");
        var pixels = new byte[side * side];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(side, side, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, side, side);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        var result = new OpenCvBarcodePrintInspector().Inspect(
            frame,
            bounds,
            symbols,
            new BarcodePrintOptions(checkQrQuietZone: true),
            default
        );
        Assert.IsTrue(
            result.Any(f => f.Code == "qr_quiet_zone_scope"),
            string.Join(";", result.Select(f => f.Message))
        );
        Assert.IsFalse(result.Any(f => f.Verdict != EInspectionVerdict.Ok));
    }

    private static ImageFrame Image(bool dirty)
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
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        // 此处在所需四模块环之外，不能把无关外部像素归为静区墨迹。
        for (int y = 1; y < 4; y++)
        {
            for (int x = 1; x < 4; x++)
            {
                pixels[y * 400 + x] = 0;
            }
        }

        if (dirty)
        {
            for (int y = 40; y < 43; y++)
            {
                for (int x = 40; x < 43; x++)
                {
                    pixels[y * 400 + x] = 0;
                }
            }
        }

        return new ImageFrame(400, 400, EImagePixelFormat.Gray8, pixels);
    }
}
