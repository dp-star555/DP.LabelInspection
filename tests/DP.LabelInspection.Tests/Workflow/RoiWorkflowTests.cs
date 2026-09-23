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

/// <summary>公开引擎阶段顺序及未完成保守失败的回归，不代表检测器准确率。</summary>
[TestClass]
public sealed partial class RoiWorkflowTests
{
    private static InspectionRegion Region(
        string name,
        int x,
        bool data = true,
        bool quality = true,
        string? guide = null
    )
    {
        return new InspectionRegion(
            name,
            ERegionKind.Text,
            new PixelRect(x, 0, 20, 20),
            true,
            new FieldSettings(expected: guide)
        ).WithTasks(new RoiInspectionTasks(data, quality));
    }

    private static InspectionRequest Request(
        IEnumerable<InspectionRegion> regions,
        IEnumerable<FieldBinding>? bindings = null,
        TaskDataSnapshot? context = null
    )
    {
        return new InspectionRequest(
            new ImageFrame(80, 40, EImagePixelFormat.Gray8, new byte[3200]),
            new InspectionRecipe(
                "stages",
                80,
                40,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                regions,
                bindings: bindings
            ),
            cycleId: "cycle",
            taskData: context
        );
    }

    private static InspectionReport Run(Backend backend, InspectionRequest request)
    {
        using var engine = new InspectionEngine(backend);
        return engine.Inspect(request);
    }

    /// <summary>四种项目组合均有明确状态，且只调用所需步骤。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SelectionControlsActualCalls(bool data, bool quality)
    {
        var b = new Backend();
        var report = Run(b, Request(new[] { Region("a", 0, data, quality) }));
        var e = report.Analysis.Regions.Single().Execution!;
        Assert.AreEqual(data, b.Calls.Contains("a:read"));
        Assert.AreEqual(quality, b.Calls.Contains("a:quality"));
        Assert.AreEqual(data ? ERoiStageState.Passed : ERoiStageState.NotRequested, e.Data);
        Assert.AreEqual(quality ? ERoiStageState.Passed : ERoiStageState.NotRequested, e.Quality);
        Assert.AreEqual(data || quality ? EInspectionVerdict.Ok : EInspectionVerdict.Ng, report.Verdict);
    }

    /// <summary>引导不符时保留原始OCR，只禁止当前ROI的质量阶段。</summary>
    [TestMethod]
    public void MismatchStopsCurrentQualityButContinuesNextRoi()
    {
        var b = new Backend();
        var report = Run(b, Request(new[] { Region("a", 0, guide: "B"), Region("b", 30) }));
        CollectionAssert.AreEqual(
            new[] { "a:validate", "a:locate", "a:read", "b:validate", "b:locate", "b:read", "b:quality" },
            b.Calls
        );
        Assert.AreEqual("A", report.Analysis.Regions[0].Recognition!.Text);
        Assert.AreEqual(ERoiStageState.NotExecuted, report.Analysis.Regions[0].Execution!.Quality);
        Assert.AreEqual(ERoiStageState.Passed, report.Analysis.Regions[1].Execution!.Quality);
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
    }

    /// <summary>昂贵工作前执行前提检查，待复核前提也保守判失败。</summary>
    [TestMethod]
    public void MissingResourcePreventsReadAndQuality()
    {
        var b = new Backend { Invalid = "a" };
        var report = Run(b, Request(new[] { Region("a", 0), Region("b", 30) }));
        Assert.IsFalse(b.Calls.Contains("a:read"));
        Assert.IsFalse(b.Calls.Contains("a:quality"));
        Assert.IsTrue(b.Calls.Contains("b:quality"));
        var a = report.Analysis.Regions[0];
        Assert.AreEqual(ERoiStageState.Failed, a.Execution!.Prerequisites);
        Assert.AreEqual(ERoiStageState.NotExecuted, a.Execution.Data);
        Assert.AreEqual(EInspectionVerdict.Ng, a.Findings.Single().Verdict);
    }

    /// <summary>异常归属于实际调用的阶段，不阻止其他ROI。</summary>
    [TestMethod]
    [DataRow("validate")]
    [DataRow("locate")]
    [DataRow("read")]
    [DataRow("quality")]
    public void ExceptionsStayLocal(string stage)
    {
        var b = new Backend { ThrowAt = "a:" + stage };
        var report = Run(b, Request(new[] { Region("a", 0), Region("b", 30) }));
        var e = report.Analysis.Regions[0].Execution!;
        Assert.IsTrue(b.Calls.Contains("b:quality"));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        if (stage == "validate" || stage == "locate")
        {
            Assert.AreEqual(ERoiStageState.Failed, e.Prerequisites);
            Assert.AreEqual(ERoiStageState.NotExecuted, e.Data);
        }

        if (stage == "read")
        {
            Assert.AreEqual(ERoiStageState.Failed, e.Data);
        }

        Assert.AreEqual(stage == "quality" ? ERoiStageState.Failed : ERoiStageState.NotExecuted, e.Quality);
    }

    /// <summary>发现列表为空不能证明未完成策略已通过。</summary>
    [TestMethod]
    public void IncompleteEmptyQualityIsNg()
    {
        var report = Run(new Backend { Complete = false }, Request(new[] { Region("a", 0, false, true) }));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsTrue(report.Analysis.Regions.Single().Findings.Any(f => f.Code == "quality_incomplete"));
    }

    /// <summary>保留多个实测缺陷，质量结果不能擦除先前数据证据。</summary>
    [TestMethod]
    public void CompleteQualityRetainsAllDefectsAndReading()
    {
        var report = Run(new Backend { Defects = 3 }, Request(new[] { Region("a", 0) }));
        var a = report.Analysis.Regions.Single();
        Assert.AreEqual("A", a.Recognition!.Text);
        Assert.AreEqual(3, a.Findings.Count(f => f.Code.StartsWith("defect_", StringComparison.Ordinal)));
        Assert.AreEqual(1, report.EvidenceGroups.Count);
        Assert.AreEqual(3, report.EvidenceGroups.Single().LocalizedCandidateCount);
        Assert.AreEqual(ERoiStageState.Failed, a.Execution!.Quality);
    }

    /// <summary>即使未选择数据项目，质量依赖也可要求实际读取。</summary>
    [TestMethod]
    public void IdentityDependencyIsReused()
    {
        var b = new Backend { NeedsRead = true };
        var report = Run(b, Request(new[] { Region("a", 0, false, true) }));
        Assert.AreEqual(1, b.Calls.Count(c => c == "a:read"));
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
    }

    /// <summary>配置的引导值不能被静默当作实际身份。</summary>
    [TestMethod]
    public void GuideWithDisabledDataFailsBeforeReading()
    {
        var b = new Backend();
        var report = Run(b, Request(new[] { Region("a", 0, false, true, "A") }));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsFalse(b.Calls.Contains("a:read"));
        Assert.IsFalse(b.Calls.Contains("a:quality"));
    }

    /// <summary>缺少任务输入时，在目标读取或质量执行前拒绝。</summary>
    [TestMethod]
    public void MissingGuideSourceIsEarlyNg()
    {
        var b = new Backend();
        var report = Run(
            b,
            Request(
                new[] { Region("a", 0) },
                new[] { new FieldBinding("a", EBindingSource.TaskData, "Part") }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsFalse(b.Calls.Contains("a:read"));
    }

    /// <summary>任务快照约束真实数据，而不是被替换的OCR值。</summary>
    [TestMethod]
    [DataRow("A", true)]
    [DataRow("B", false)]
    public void TaskGuidePrecedesQuality(string expected, bool passes)
    {
        var now = DateTimeOffset.UtcNow;
        var data = new TaskDataSnapshot(
            "cycle",
            "test",
            now.AddMinutes(-1),
            now.AddMinutes(1),
            new Dictionary<string, string> { { "Part", expected } }
        );
        var b = new Backend();
        var report = Run(
            b,
            Request(
                new[] { Region("a", 0) },
                new[] { new FieldBinding("a", EBindingSource.TaskData, "Part") },
                data
            )
        );
        Assert.AreEqual(passes, b.Calls.Contains("a:quality"));
        Assert.AreEqual("A", report.Analysis.Regions.Single().Recognition!.Text);
    }

    /// <summary>依赖可先执行，但结果保留配方顺序且只读取一次。</summary>
    [TestMethod]
    public void CrossRoiDependencyIsCached()
    {
        var b = new Backend();
        var report = Run(
            b,
            Request(
                new[] { Region("a", 0), Region("b", 30, true, false) },
                new[] { new FieldBinding("a", EBindingSource.Region, "b") }
            )
        );
        Assert.IsTrue(b.Calls.IndexOf("b:read") < b.Calls.IndexOf("a:read"));
        Assert.AreEqual(1, b.Calls.Count(c => c == "b:read"));
        CollectionAssert.AreEqual(
            new[] { "a", "b" },
            report.Analysis.Regions.Select(r => r.RegionName).ToArray()
        );
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
    }

    /// <summary>循环依赖不能虚构互相认证的读数。</summary>
    [TestMethod]
    public void DependencyCycleFailsWithoutReading()
    {
        var b = new Backend();
        var report = Run(
            b,
            Request(
                new[] { Region("a", 0), Region("b", 30) },
                new[]
                {
                    new FieldBinding("a", EBindingSource.Region, "b"),
                    new FieldBinding("b", EBindingSource.Region, "a"),
                }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsFalse(b.Calls.Any(c => c.EndsWith(":read", StringComparison.Ordinal)));
        Assert.AreEqual(2, report.Analysis.Regions.Count);
    }

    /// <summary>显式项目选择经JSON保留；无选择字段的配方使用安全历史默认值。</summary>
    [TestMethod]
    public void TasksRoundTripAndLegacyDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "dp-roi-tasks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InspectionStore(root, new OpenCvImageCodec());
            var recipe = Request(new[] { Region("a", 0, false, true) }).Recipe;
            var copy = store.DeserializeRecipe(store.SerializeRecipe(recipe));
            Assert.IsFalse(copy.Regions.Single().Tasks.ReadData);
            Assert.IsTrue(copy.Regions.Single().Tasks.CheckQuality);
            var legacy = new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(0, 0, 20, 20));
            Assert.IsFalse(legacy.Tasks.ReadData);
            Assert.IsTrue(legacy.Tasks.CheckQuality);
            var request = Request(copy.Regions);
            string job = store.SaveReport(request, Run(new Backend(), request));
            string json = File.ReadAllText(Path.Combine(root, "jobs", job, "report.json"));
            StringAssert.Contains(json, "\"execution\"");
            StringAssert.Contains(json, "\"prerequisites\"");
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
