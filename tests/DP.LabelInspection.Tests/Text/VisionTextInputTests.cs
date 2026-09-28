using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

/// <summary>Vision图像租约和中立OCR直接接入正式文字读取与内容核对。</summary>
[TestClass]
public sealed class VisionTextInputTests
{
    /// <summary>Vision识别器借用当前帧，报告保留真实OCR和帧身份。</summary>
    [TestMethod]
    public void VisionRecognizerReadsBorrowedFrameWithoutLabelRecognizer()
    {
        using var pixels = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(48, 24, DP.Vision.EPixelLayout.Gray8),
            new byte[48 * 24]
        );
        using var frame = new DP.Vision.ImageFrame("text-frame", pixels);
        var roi = new InspectionRegion(
            "text",
            ERegionKind.Text,
            new PixelRect(0, 0, 48, 24),
            true,
            new FieldSettings(expected: "A")
        ).WithTasks(new RoiInspectionTasks(true, false));
        using var request = InspectionRequest.FromVision(
            frame,
            new InspectionRecipe(
                "ocr",
                48,
                24,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            )
        );
        var recognizer = new Recognizer();
        using var backend = new OpenCvInspectionBackend(recognizer: recognizer);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual("A", report.Analysis.Regions.Single().Recognition!.Text);
        Assert.AreEqual("text-frame", report.Analysis.Alignment!.FrameId);
        Assert.AreEqual(1, recognizer.Calls);
        Assert.AreEqual(EInspectionCapabilities.Ocr, backend.Capabilities & EInspectionCapabilities.Ocr);
    }

    private sealed class Recognizer : A.ITextLineRecognizer
    {
        public int Calls { get; private set; }

        public A.TextLineRecognition Recognize(
            DP.Vision.IImageSource frame,
            A.PixelBounds bounds,
            CancellationToken token
        )
        {
            Assert.AreEqual(48, frame.Info.Width);
            Calls++;
            return new A.TextLineRecognition(
                bounds,
                "test-model",
                48,
                48,
                new[] { new A.CtcStep(1, .99f) },
                new[] { new A.CtcToken("A", 0, 1, .99f) }
            );
        }

        public void Dispose() { }
    }
}
