using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>无需下载模型的解码器、预处理及OCR编排回归。</summary>
[TestClass]
public sealed partial class TextRecognitionTests
{
    /// <summary>空白分隔重复字符、相邻重复折叠，首尾空白仍保留在诊断中。</summary>
    [TestMethod]
    public void CtcRunsRespectBlankAndRepeat()
    {
        var steps = new[] { 0, 1, 1, 0, 1, 2, 2, 0 }.Select(i => new CtcStep(i, .9f)).ToArray();
        var tokens = CtcDecoder.Decode(steps, new[] { "", "A", "B" });
        Assert.AreEqual("AAB", string.Concat(tokens.Select(t => t.Text)));
        Assert.AreEqual(1, tokens[0].Start);
        Assert.AreEqual(3, tokens[0].End);
        Assert.AreEqual(4, tokens[1].Start);
        Assert.AreEqual(7, tokens[2].End);
    }

    /// <summary>空识别不能伪造内容。</summary>
    [TestMethod]
    public void AllBlankIsEmpty()
    {
        Assert.AreEqual(0, CtcDecoder.Decode(new[] { new CtcStep(0, 1) }, new[] { "", "A" }).Count);
    }

    /// <summary>无效模型索引或概率明确失败。</summary>
    [TestMethod]
    public void InvalidCtcRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CtcDecoder.Decode(new[] { new CtcStep(2, 1) }, new[] { "", "A" })
        );
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CtcStep(1, float.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CtcStep(1, float.PositiveInfinity));
    }

    /// <summary>BGR通道顺序及零填充而非白色填充符合模型预处理契约。</summary>
    [TestMethod]
    public void BgrChannelsAndPadding()
    {
        var frame = new ImageFrame(1, 1, EImagePixelFormat.Bgr24, new byte[] { 0, 127, 255 });
        var input = new OpenCvTextLinePreprocessor().Prepare(frame, new PixelRect(0, 0, 1, 1), default);
        var values = input.CopyValues();
        Assert.AreEqual(320, input.Width);
        Assert.AreEqual(48, input.ContentWidth);
        Assert.AreEqual(-1f, values[0]);
        Assert.AreEqual((127 / 255f - .5f) / .5f, values[48 * 320]);
        Assert.AreEqual(1f, values[2 * 48 * 320]);
        Assert.AreEqual(0f, values[48]);
        values[0] = 1;
        Assert.AreEqual(-1f, input.CopyValues()[0]);
    }

    /// <summary>遵守明确裁剪，Gray8按相同规则扩展。</summary>
    [TestMethod]
    public void GrayCropAndLongWidth()
    {
        var pixels = new byte[700 * 48];
        pixels[0] = 255;
        var frame = new ImageFrame(700, 48, EImagePixelFormat.Gray8, pixels);
        var prep = new OpenCvTextLinePreprocessor();
        var tiny = prep.Prepare(frame, new PixelRect(0, 0, 1, 1), default).CopyValues();
        Assert.AreEqual(1f, tiny[0]);
        Assert.AreEqual(1f, tiny[2 * 48 * 320]);
        var wide = prep.Prepare(frame, new PixelRect(0, 0, 700, 48), default);
        Assert.AreEqual(700, wide.Width);
        Assert.AreEqual(700, wide.ContentWidth);
    }

    /// <summary>无效几何或已取消的预处理不会进入推理。</summary>
    [TestMethod]
    public void PreprocessingRejectsInvalidInput()
    {
        var prep = new OpenCvTextLinePreprocessor();
        var frame = new ImageFrame(100, 1, EImagePixelFormat.Gray8, new byte[100]);
        Assert.ThrowsExactly<ArgumentException>(() =>
            prep.Prepare(frame, new PixelRect(0, 0, 100, 1), default)
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            prep.Prepare(frame, new PixelRect(99, 0, 2, 1), default)
        );
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            prep.Prepare(frame, new PixelRect(0, 0, 1, 1), new CancellationToken(true))
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            new InspectionRegion("bad", ERegionKind.Blank, new PixelRect(0, 0, 1, 1), true)
        );
    }

    /// <summary>逼真的注入观测经过质量关卡仍保留，但不证明字符外观合格。</summary>
    [TestMethod]
    public void RecognitionSurvivesQualityGate()
    {
        using var recognizer = new FakeRecognizer();
        using var backend = new OpenCvInspectionBackend(recognizer);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(Request(true));
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual("A", report.Analysis.Regions[0].Recognition!.Text);
        Assert.AreEqual(2, report.Analysis.Regions[0].Recognition!.Bounds.X);
        Assert.IsFalse(report.Findings.Any(f => f.Code == "image_quality_review"));
        Assert.IsTrue(backend.Capabilities.HasFlag(EInspectionCapabilities.Ocr));
        Assert.IsTrue(backend.Capabilities.HasFlag(EInspectionCapabilities.CharacterSegmentation));
        Assert.IsNull(report.Analysis.Regions[0].Segmentation);
    }

    /// <summary>多行、未声明布局及忽略证据不能被静默识别。</summary>
    [TestMethod]
    public void RequiresSingleLineAndUnmaskedEvidence()
    {
        using var recognizer = new FakeRecognizer();
        using var backend = new OpenCvInspectionBackend(recognizer);
        using var engine = new InspectionEngine(backend);
        Assert.AreEqual(
            "text_layout_required",
            engine.Inspect(Request(false)).Analysis.Regions[0].Findings[0].Code
        );
        Assert.AreEqual(
            "ignored_read_scope",
            engine.Inspect(Request(true, true)).Analysis.Regions[0].Findings[0].Code
        );
        Assert.AreEqual(0, recognizer.Calls);
    }

    /// <summary>识别器所有权明确，释放幂等。</summary>
    [TestMethod]
    public void RecognizerOwnership()
    {
        var borrowed = new FakeRecognizer();
        new OpenCvInspectionBackend(borrowed).Dispose();
        Assert.AreEqual(0, borrowed.Disposals);
        borrowed.Dispose();
        var owned = new FakeRecognizer();
        var backend = new OpenCvInspectionBackend(owned, true);
        backend.Dispose();
        backend.Dispose();
        Assert.AreEqual(1, owned.Disposals);
    }

    /// <summary>明确预期内容取代无条件身份警告，但不因此批准外观。</summary>
    [TestMethod]
    [DataRow("A", .8, 0.0, "content_match", EInspectionVerdict.Ok)]
    [DataRow("B", .8, 0.0, "content_mismatch", EInspectionVerdict.Ng)]
    [DataRow("A", .95, 0.0, "ocr_unreliable", EInspectionVerdict.Ng)]
    [DataRow("B", .95, 0.0, "ocr_unreliable", EInspectionVerdict.Ng)]
    [DataRow("A", .8, 35.0, "content_match", EInspectionVerdict.Ok)]
    [DataRow("B", .8, 35.0, "content_mismatch", EInspectionVerdict.Ng)]
    public void ExpectedContentHasExplicitOutcome(
        string expected,
        double confidence,
        double contrast,
        string code,
        EInspectionVerdict verdict
    )
    {
        using var recognizer = new FakeRecognizer();
        using var backend = new OpenCvInspectionBackend(recognizer);
        using var engine = new InspectionEngine(backend);
        var frame = new ImageFrame(20, 20, EImagePixelFormat.Gray8, new byte[400]);
        var region = new InspectionRegion(
            "text",
            ERegionKind.Text,
            new PixelRect(2, 2, 10, 10),
            true,
            new FieldSettings(expected: expected, minimumConfidence: confidence)
        );
        var report = engine.Inspect(
            new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "expected",
                    20,
                    20,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { region },
                    new InspectionOptions(minimumContrast: contrast, minimumSharpness: 0)
                )
            )
        );
        var findings = report.Analysis.Regions[0].Findings;
        Assert.IsFalse(findings.Any(f => f.Code == "ocr_identity_review"));
        Assert.AreEqual(verdict, findings.Single(f => f.Code == code).Verdict);
        Assert.AreEqual(
            "A",
            report.Analysis.Regions[0].Recognition!.Text,
            "Raw OCR must not be rewritten to the expected value."
        );
        Assert.AreEqual(verdict, report.Verdict, "Only selected projects determine scoped ROI acceptance.");
    }

    /// <summary>无外观绑定时，纯文本用途不尝试分割，也不报告字形覆盖缺失。</summary>
    [TestMethod]
    public void UnboundTextDoesNotRequestAppearance()
    {
        using var recognizer = new FakeRecognizer();
        using var backend = new OpenCvInspectionBackend(
            recognizer,
            segmenter: new RejectUnexpectedSegmentation()
        );
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(Request(true));
        var result = report.Analysis.Regions.Single();
        Assert.AreEqual("A", result.Recognition!.Text);
        Assert.IsNull(result.Segmentation);
        Assert.AreEqual(0, result.Glyphs.Count);
        Assert.IsFalse(
            result.Findings.Any(f =>
                f.Code == "glyph_library_unbound"
                || f.Code == "no_character_coverage"
                || f.Code == "segmentation_review"
            )
        );
    }

    /// <summary>显式外观绑定即使内容可读，仍要求分割能力和参考仓库。</summary>
    [TestMethod]
    public void BoundTextStillReportsUninspectedAppearance()
    {
        using var recognizer = new FakeRecognizer();
        using var backend = new OpenCvInspectionBackend(recognizer);
        using var engine = new InspectionEngine(backend);
        var input = Request(true);
        var recipe = new InspectionRecipe(
            "bound",
            20,
            20,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[]
            {
                new InspectionRegion(
                    "text",
                    ERegionKind.Text,
                    new PixelRect(2, 2, 10, 10),
                    true,
                    new FieldSettings("selected-library", 1)
                ),
            }
        );
        var result = engine.Inspect(new InspectionRequest(input.Actual, recipe)).Analysis.Regions.Single();
        Assert.IsNull(result.Segmentation);
        Assert.IsNull(result.Recognition);
        Assert.AreEqual(0, recognizer.Calls);
        Assert.IsTrue(result.Findings.Any(f => f.Code == "glyph_repository_unavailable"));
        Assert.AreEqual(ERoiStageState.Failed, result.Execution!.Prerequisites);
    }

    private static InspectionRequest Request(bool single, bool ignore = false)
    {
        var regions = new[]
        {
            new InspectionRegion("text", ERegionKind.Text, new PixelRect(2, 2, 10, 10), single),
        }.AsEnumerable();
        if (ignore)
        {
            regions = regions.Concat(
                new[] { new InspectionRegion("ignore", ERegionKind.Ignore, new PixelRect(2, 2, 1, 1)) }
            );
        }

        return new InspectionRequest(
            new ImageFrame(20, 20, EImagePixelFormat.Gray8, new byte[400]),
            new InspectionRecipe("text", 20, 20, EInspectionMode.Free, EAlignmentMode.AssumeAligned, regions)
        );
    }
}
