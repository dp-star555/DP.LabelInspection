using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>通过公开引擎验证独立周期数据与跨ROI约束。</summary>
[TestClass]
public sealed partial class FieldBindingTests
{
    /// <summary>原始精确比较不修正O/0，也不信任有歧义的条码。</summary>
    [TestMethod]
    [DataRow("A", "A", 1, .99, 255.0, "binding_match", EInspectionVerdict.Ok)]
    [DataRow("O", "0", 1, .99, 255.0, "binding_mismatch", EInspectionVerdict.Ng)]
    [DataRow("a", "A", 1, .99, 255.0, "binding_mismatch", EInspectionVerdict.Ng)]
    [DataRow("A", "A", 0, .99, 255.0, "binding_review", EInspectionVerdict.Review)]
    [DataRow("A", "A", 2, .99, 255.0, "binding_review", EInspectionVerdict.Review)]
    [DataRow("A", "A", 1, .4, 255.0, "binding_review", EInspectionVerdict.Review)]
    [DataRow("A", "B", 1, .99, 0.0, "binding_review", EInspectionVerdict.Review)]
    public void CrossRegion(
        string text,
        string barcode,
        int count,
        double confidence,
        double contrast,
        string code,
        EInspectionVerdict verdict
    )
    {
        using var backend = new Backend(text, barcode, count, confidence, contrast);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(Request(new FieldBinding("text", EBindingSource.Region, "barcode")));
        var result = report.Analysis.Regions.Single(r => r.RegionName == "text");
        Assert.AreEqual(verdict, result.Findings.Single(f => f.Code == code).Verdict);
        Assert.IsFalse(result.Findings.Any(f => f.Code == "ocr_identity_review"));
        Assert.AreEqual(text, result.Recognition!.Text);
        Assert.AreNotEqual(EInspectionVerdict.Ok, report.Verdict);
    }

    /// <summary>任务值被复制并绑定采集周期，具有有效期且包含在报告导出中。</summary>
    [TestMethod]
    [DataRow("ok", "binding_match", EInspectionVerdict.Ok)]
    [DataRow("different", "binding_mismatch", EInspectionVerdict.Ng)]
    [DataRow("missing", "binding_review", EInspectionVerdict.Review)]
    [DataRow("cycle", "binding_review", EInspectionVerdict.Review)]
    [DataRow("expired", "binding_review", EInspectionVerdict.Review)]
    [DataRow("future", "binding_review", EInspectionVerdict.Review)]
    [DataRow("none", "binding_review", EInspectionVerdict.Review)]
    public void TaskContext(string state, string code, EInspectionVerdict verdict)
    {
        var now = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, string> { { "Part", state == "different" ? "B" : "A" } };
        if (state == "missing")
        {
            values.Clear();
        }

        var snapshot = new TaskDataSnapshot(
            "cycle-1",
            "MES/test",
            state == "future" ? now.AddHours(1) : now.AddHours(-2),
            state == "expired" ? now.AddHours(-1) : now.AddHours(2),
            values
        );
        values["Part"] = "modified-after-snapshot";
        using var backend = new Backend();
        using var engine = new InspectionEngine(backend);
        var request = Request(
            new FieldBinding("text", EBindingSource.TaskData, "Part"),
            state == "none" ? null : snapshot,
            state == "cycle" ? "wrong" : "cycle-1"
        );
        var report = engine.Inspect(request);
        Assert.AreEqual(
            verdict,
            report
                .Analysis.Regions.Single(r => r.RegionName == "text")
                .Findings.Single(f => f.Code == code)
                .Verdict
        );
        if (state == "ok")
        {
            string root = Path.Combine(Path.GetTempPath(), "dp-binding-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new InspectionStore(root, new OpenCvImageCodec());
                var recipe = store.DeserializeRecipe(store.SerializeRecipe(request.Recipe));
                Assert.AreEqual("Part", recipe.Bindings.Single().Key);
                string job = store.SaveReport(request, report);
                string context = File.ReadAllText(Path.Combine(root, "jobs", job, "task-context.json"));
                Assert.IsTrue(context.Contains("MES/test"));
                Assert.IsFalse(context.Contains("modified-after-snapshot"));
                store.ExportReport(job, Path.Combine(root, "report.zip"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }

    /// <summary>绑定不能指向自身、未知名称或不可读区域。</summary>
    [TestMethod]
    public void InvalidReferencesFailEarly()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new FieldBinding("text", EBindingSource.Region, "text")
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            Request(new FieldBinding("text", EBindingSource.Region, "absent"))
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            Request(new FieldBinding("absent", EBindingSource.TaskData, "Part"))
        );
    }

    /// <summary>跨ROI一致不能掩盖与独立任务值不符；历史非分阶段策略的环比较原始观测。</summary>
    [TestMethod]
    public void MultipleConstraintsAndCyclesRemainIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var data = new TaskDataSnapshot(
            "c",
            "MES",
            now.AddMinutes(-1),
            now.AddMinutes(1),
            new Dictionary<string, string> { { "Part", "B" } }
        );
        var initial = Request(new FieldBinding("text", EBindingSource.Region, "barcode"));
        var bindings = new List<FieldBinding>
        {
            new FieldBinding("text", EBindingSource.Region, "barcode"),
            new FieldBinding("barcode", EBindingSource.Region, "text"),
            new FieldBinding("text", EBindingSource.TaskData, "Part"),
        };
        var recipe = new InspectionRecipe(
            "combined",
            80,
            40,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            initial.Recipe.Regions,
            bindings: bindings
        );
        bindings.Clear();
        Assert.AreEqual(3, recipe.Bindings.Count);
        using var backend = new Backend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(initial.Actual, recipe, cycleId: "c", taskData: data)
        );
        var findings = report.Analysis.Regions.SelectMany(r => r.Findings).ToArray();
        Assert.AreEqual(2, findings.Count(f => f.Code == "binding_match"));
        Assert.AreEqual(1, findings.Count(f => f.Code == "binding_mismatch"));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
    }

    /// <summary>条码ROI可包含其可读文字，两种观测仍须独立比较。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OverlappingTextAndBarcodeCanBind(bool reverse)
    {
        var frame = new ImageFrame(80, 40, EImagePixelFormat.Gray8, new byte[3200]);
        var regions = new[]
        {
            new InspectionRegion("text", ERegionKind.Text, new PixelRect(10, 10, 30, 20), true),
            new InspectionRegion("barcode", ERegionKind.Barcode, new PixelRect(0, 0, 60, 35)),
        };
        using var backend = new Backend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "overlap",
                    80,
                    40,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    reverse ? regions.Reverse() : regions,
                    bindings: new[] { new FieldBinding("text", EBindingSource.Region, "barcode") }
                )
            )
        );
        Assert.AreEqual(
            EInspectionVerdict.Ok,
            report
                .Analysis.Regions.Single(r => r.RegionName == "text")
                .Findings.Single(f => f.Code == "binding_match")
                .Verdict
        );
    }

    private static InspectionRequest Request(
        FieldBinding binding,
        TaskDataSnapshot? data = null,
        string? cycle = null
    )
    {
        var frame = new ImageFrame(80, 40, EImagePixelFormat.Gray8, new byte[3200]);
        return new InspectionRequest(
            frame,
            new InspectionRecipe(
                "binding",
                80,
                40,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[]
                {
                    new InspectionRegion("text", ERegionKind.Text, new PixelRect(0, 0, 30, 30), true),
                    new InspectionRegion("barcode", ERegionKind.Barcode, new PixelRect(40, 0, 30, 30)),
                },
                bindings: new[] { binding }
            ),
            cycleId: cycle,
            taskData: data
        );
    }
}
