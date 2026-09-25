using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
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
        var frame = new ImageFrame(size, size, EImagePixelFormat.Gray8, pixels);
        var symbol = new ZxingBarcodeDecoder()
            .Decode(frame, new PixelRect(0, 0, size, size), default)
            .Single();
        Assert.AreEqual("", symbol.Preprocessing);
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

        var frame = new ImageFrame(size, size, EImagePixelFormat.Gray8, pixels);
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

        var frame = new ImageFrame(size, size, EImagePixelFormat.Gray8, spread);
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
