using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>内容核对规则脱离调度流程的直接测试：取值、码制、任务数据有效性、格式约束与精确比较。</summary>
[TestClass]
public sealed class ContentVerificationTests
{
    private static readonly PixelRect Bounds = new PixelRect(1, 2, 30, 10);

    /// <summary>文字取OCR文本；码只有唯一一个时取其内容，没有或多个时不可用。</summary>
    [TestMethod]
    public void ValueRequiresUniqueReading()
    {
        var text = new RegionInspectionResult(
            "t",
            Array.Empty<InspectionFinding>(),
            new TextLineRecognition(
                Bounds,
                "m",
                320,
                48,
                new[] { new CtcStep(1, .9f) },
                new[] { new CtcToken("AB1", 0, 1, .9f) }
            )
        );
        Assert.IsTrue(ContentVerification.TryGetValue(text, out var value));
        Assert.AreEqual("AB1", value);
        RegionInspectionResult Codes(int n) =>
            new RegionInspectionResult(
                "c",
                Array.Empty<InspectionFinding>(),
                barcodes: Enumerable
                    .Range(0, n)
                    .Select(i => new BarcodeObservation("X" + i, "CODE_128", Bounds))
            );
        Assert.IsTrue(ContentVerification.TryGetValue(Codes(1), out value));
        Assert.AreEqual("X0", value);
        Assert.IsFalse(ContentVerification.TryGetValue(Codes(0), out _));
        Assert.IsFalse(ContentVerification.TryGetValue(Codes(2), out _));
    }

    /// <summary>自动类型接受任意码制；QR只接受QR_CODE；一维码类型只接受一维码制。</summary>
    [TestMethod]
    public void BarcodeKind()
    {
        Assert.IsTrue(ContentVerification.BarcodeKindMatches("QR_CODE", EBarcodeKind.Auto));
        Assert.IsTrue(ContentVerification.BarcodeKindMatches("QR_CODE", EBarcodeKind.QrCode));
        Assert.IsFalse(ContentVerification.BarcodeKindMatches("CODE_128", EBarcodeKind.QrCode));
        var linear = Enum.GetValues(typeof(EBarcodeKind))
            .Cast<EBarcodeKind>()
            .First(k => k != EBarcodeKind.Auto && k != EBarcodeKind.QrCode);
        Assert.IsTrue(ContentVerification.BarcodeKindMatches("CODE_128", linear));
        Assert.IsFalse(ContentVerification.BarcodeKindMatches("QR_CODE", linear));
    }

    /// <summary>任务字段须有数据、周期一致、在有效期内且含该字段，否则给出不可用原因。</summary>
    [TestMethod]
    [DataRow("ok", true, "")]
    [DataRow("none", false, "未提供本周期任务引导数据。")]
    [DataRow("cycle", false, "图像与引导数据的周期不一致。")]
    [DataRow("expired", false, "引导数据尚未生效或已经过期。")]
    [DataRow("future", false, "引导数据尚未生效或已经过期。")]
    [DataRow("missing", false, "缺少任务引导字段：Part")]
    public void TaskValueValidity(string state, bool available, string reason)
    {
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        var values = new Dictionary<string, string>();
        if (state != "missing")
        {
            values["Part"] = "A1";
        }

        var data = new TaskDataSnapshot(
            "c1",
            "MES",
            state == "future" ? now.AddMinutes(5) : now.AddMinutes(-5),
            state == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            values
        );
        var request = TestRequests.FromSnapshot(
            new ImageFrame(8, 8, EImagePixelFormat.Gray8, new byte[64]),
            new InspectionRecipe(
                "r",
                8,
                8,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                Array.Empty<InspectionRegion>()
            ),
            cycleId: state == "cycle" ? "c2" : "c1",
            taskData: state == "none" ? null : data
        );
        Assert.AreEqual(
            available,
            ContentVerification.TryGetTaskValue(request, "Part", now, out var value, out var why)
        );
        Assert.AreEqual(reason, why);
        Assert.AreEqual(available ? "A1" : null, value);
    }

    /// <summary>引导值不一致时逐位列出同长度的字符差异；长度、字符集、整段格式各自报告；全部符合时没有发现。</summary>
    [TestMethod]
    public void RulesReportEachViolation()
    {
        var expected = ContentVerification.CheckRules("AB0", new FieldSettings(expected: "ABO"), Bounds);
        CollectionAssert.AreEqual(
            new[] { "content_mismatch", "content_character_mismatch" },
            expected.Select(f => f.Code).ToArray()
        );
        Assert.IsTrue(expected.All(f => f.Verdict == EInspectionVerdict.Ng && Equals(f.Bounds, Bounds)));
        StringAssert.Contains(expected[1].Message, "文本偏移2");

        var format = ContentVerification.CheckRules(
            "ab-1",
            new FieldSettings(pattern: "[A-Z]{2}\\d", allowedCharacters: "ABCD0123456789", maximumLength: 3),
            Bounds
        );
        CollectionAssert.AreEqual(
            new[] { "length_mismatch", "charset_mismatch", "pattern_mismatch" },
            format.Select(f => f.Code).ToArray()
        );

        Assert.AreEqual(
            0,
            ContentVerification
                .CheckRules(
                    "AB1",
                    new FieldSettings(expected: "AB1", pattern: "[A-Z]{2}\\d", minimumLength: 3),
                    Bounds
                )
                .Count
        );
    }

    /// <summary>与引导值逐字符精确比较，不纠正O/0或大小写；每个不一致的引导值一条NG。</summary>
    [TestMethod]
    public void GuidesCompareExactly()
    {
        Assert.AreEqual(0, ContentVerification.CompareGuides("A0", new[] { "A0", "A0" }, Bounds).Count);
        var mismatches = ContentVerification.CompareGuides("A0", new[] { "AO", "a0", "A0" }, Bounds);
        Assert.AreEqual(2, mismatches.Count);
        Assert.IsTrue(
            mismatches.All(f => f.Code == "binding_mismatch" && f.Verdict == EInspectionVerdict.Ng)
        );
        StringAssert.Contains(mismatches[0].Message, "引导值=[AO]");
    }
}
