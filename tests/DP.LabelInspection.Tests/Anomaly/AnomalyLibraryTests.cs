using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 方法B（局部块异常检测）接入检测流程：模型库与字库一样按固定版本管理和绑定；A、B可单独或同时启用，任一NG即NG。
/// </summary>
[TestClass]
public sealed class AnomalyLibraryTests
{
    private static readonly PixelRect Box = new PixelRect(30, 20, 150, 50);

    private static ImageFrame Label(int shift, int seed, bool defect = false)
    {
        using var m = new Mat(80, 220, MatType.CV_8UC1, Scalar.All(235));
        Cv2.Randn(m, Scalar.All(235), Scalar.All(3));
        Cv2.PutText(
            m,
            "AB12",
            new Point(40 + shift, 60),
            HersheyFonts.HersheySimplex,
            1.2,
            Scalar.All(25),
            3
        );
        if (defect)
        {
            Cv2.Rectangle(m, new Rect(70 + shift, 42, 8, 5), Scalar.All(235), -1);
        }

        var bytes = new byte[m.Rows * m.Cols];
        Marshal.Copy(m.Data, bytes, 0, bytes.Length);
        return new ImageFrame(m.Cols, m.Rows, EImagePixelFormat.Gray8, bytes);
    }

    private sealed class Temp : IDisposable
    {
        internal readonly string Root = Path.Combine(
            Path.GetTempPath(),
            "dp-anomaly-library-" + Guid.NewGuid().ToString("N")
        );

        internal readonly InspectionStore Store;

        internal Temp()
        {
            Store = new InspectionStore(Root, new OpenCvImageCodec());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    private static AnomalyModelEntry Train(InspectionRegion region)
    {
        return new RegionAnomalyDetector().TrainEntry(
            new[] { Label(0, 1), Label(1, 2), Label(0, 3) },
            region
        );
    }

    private static InspectionRegion Fixed(bool a, bool b, AnomalySettings? pin)
    {
        return new InspectionRegion("fixed", ERegionKind.Fixed, Box, anomaly: pin).WithTasks(
            new RoiInspectionTasks(false, a, b)
        );
    }

    private static RegionInspectionResult Run(
        InspectionStore store,
        InspectionRegion region,
        ImageFrame actual,
        ImageFrame? reference = null
    )
    {
        using var backend = new OpenCvInspectionBackend(anomalyModels: store.AnomalyLibraries);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
                actual,
                new InspectionRecipe(
                    "anomaly",
                    220,
                    80,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { region }
                ),
                reference
            )
        );
        return report.Analysis.Regions.Single();
    }

    private static bool Ng(RegionInspectionResult r)
    {
        return r.Findings.Any(f => f.Verdict != EInspectionVerdict.Ok);
    }

    /// <summary>模型库按不可变版本发布：过期编辑被拒绝，旧版本仍可读取，导出后可导入为新库。</summary>
    [TestMethod]
    public void LibraryIsVersionedLikeGlyphLibraries()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("产品A");
        var entry = Train(Fixed(false, true, null));
        Assert.AreEqual(2, models.PutAnomalyModel(id, 1, entry));
        Assert.ThrowsExactly<InvalidOperationException>(() => models.PutAnomalyModel(id, 1, entry));
        Assert.ThrowsExactly<InvalidOperationException>(() => models.PutAnomalyModel(id, 2, entry));
        Assert.AreEqual(3, models.PutAnomalyModel(id, 2, entry.WithKey("copy"), provenanceJson: "{\"n\":3}"));

        var two = models.LoadAnomalyLibrary(id, 2);
        Assert.AreEqual(1, two.Models.Count);
        var loaded = two.Models["fixed"];
        Assert.AreEqual(entry.Sha256, loaded.Sha256);
        CollectionAssert.AreEqual(entry.CopyModel(), loaded.CopyModel());
        Assert.AreEqual(entry.Width, loaded.Width);
        Assert.AreEqual(3, loaded.LocalRadius);
        Assert.AreEqual(2, models.LoadAnomalyLibrary(id, 3).Models.Count);
        // 同一模型字节在多个版本、多个键之间只保存一份。
        Assert.AreEqual(
            1,
            Directory.GetFiles(Path.Combine(temp.Root, "anomaly-libraries", id, "models")).Length
        );

        Assert.AreEqual(4, models.RemoveAnomalyModel(id, 3, "copy"));
        Assert.AreEqual(5, models.ArchiveAnomalyLibrary(id, 4));
        Assert.AreEqual(0, models.ListAnomalyLibraries().Count);
        Assert.AreEqual(1, models.ListAnomalyLibraries(includeArchived: true).Single().Models);
        Assert.ThrowsExactly<InvalidOperationException>(() => models.PutAnomalyModel(id, 5, entry, true));

        string imported = models.ImportAnomalyLibrary(models.ExportAnomalyLibrary(id, 3));
        Assert.AreNotEqual(id, imported);
        var copy = models.LoadAnomalyLibrary(imported, 1);
        Assert.AreEqual(2, copy.Models.Count);
        CollectionAssert.AreEqual(entry.CopyModel(), copy.Models["copy"].CopyModel());
    }

    /// <summary>配方保存B开关和固定版本绑定；旧配方没有B字段时按未启用读取。</summary>
    [TestMethod]
    public void RecipeKeepsSwitchAndPin()
    {
        using var temp = new Temp();
        var recipe = new InspectionRecipe(
            "r",
            220,
            80,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[] { Fixed(false, true, new AnomalySettings("anomaly-x", 4, "key")) }
        );
        string json = temp.Store.SerializeRecipe(recipe);
        var back = temp.Store.DeserializeRecipe(json).Regions.Single();
        Assert.IsTrue(back.Tasks.DetectAnomaly);
        Assert.IsFalse(back.Tasks.CheckQuality);
        Assert.AreEqual("anomaly-x", back.Anomaly!.LibraryId);
        Assert.AreEqual(4, back.Anomaly.LibraryRevision);
        Assert.AreEqual("key", back.Anomaly.KeyFor(back.Name));

        var legacy = Newtonsoft.Json.Linq.JObject.Parse(json);
        var region = (Newtonsoft.Json.Linq.JObject)legacy["regions"]![0]!;
        region.Remove("anomaly");
        ((Newtonsoft.Json.Linq.JObject)region["tasks"]!).Remove("detectAnomaly");
        ((Newtonsoft.Json.Linq.JObject)region["tasks"]!)["checkQuality"] = true;
        var old = temp.Store.DeserializeRecipe(legacy.ToString()).Regions.Single();
        Assert.IsFalse(old.Tasks.DetectAnomaly);
        Assert.IsTrue(old.Tasks.CheckQuality);
        Assert.IsNull(old.Anomaly);
    }

    /// <summary>只启用B：良品通过并给出得分摘要和热力图；缺口NG，框落在ROI内；方法A未执行。</summary>
    [TestMethod]
    public void AnomalyOnlyJudgesQuality()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("产品A");
        int revision = models.PutAnomalyModel(id, 1, Train(Fixed(false, true, null)));
        var region = Fixed(false, true, new AnomalySettings(id, revision));

        var good = Run(temp.Store, region, Label(1, 4));
        Assert.IsFalse(Ng(good), string.Join(";", good.Findings.Select(f => f.Code + ":" + f.Message)));
        Assert.AreEqual(ERoiStageState.Passed, good.Execution!.Quality);
        Assert.IsTrue(good.Findings.Any(f => f.Code == "anomaly_summary"));
        Assert.AreEqual(good.Anomaly!.Crop.Width, good.Anomaly.HeatMap!.Width);
        Assert.IsTrue(good.Anomaly.Ratio < 1);

        var bad = Run(temp.Store, region, Label(0, 5, defect: true));
        Assert.IsTrue(Ng(bad));
        Assert.AreEqual(ERoiStageState.Failed, bad.Execution!.Quality);
        var b = bad
            .Findings.First(f => f.Verdict == EInspectionVerdict.Ng && f.Bounds.HasValue)
            .Bounds!.Value;
        Assert.IsTrue(b.X <= 78 && b.X + b.Width >= 70 && b.Y <= 47 && b.Y + b.Height >= 42, b.ToString());
        Assert.IsTrue(bad.Anomaly!.Ratio > 1);
    }

    /// <summary>A+B同时启用：两者都执行，任一NG即NG；只启用A时不产生B证据。</summary>
    [TestMethod]
    public void BothMethodsRunAndEitherFails()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("产品A");
        int revision = models.PutAnomalyModel(id, 1, Train(Fixed(false, true, null)));
        var pin = new AnomalySettings(id, revision);
        var reference = Label(0, 1);

        var both = Run(temp.Store, Fixed(true, true, pin), Label(0, 5, defect: true), reference);
        Assert.IsTrue(Ng(both));
        Assert.IsNotNull(both.Anomaly);
        Assert.IsTrue(both.Findings.Any(f => f.Code == "anomaly_summary"));

        var aOnly = Run(temp.Store, Fixed(true, false, pin), Label(1, 4), reference);
        Assert.IsNull(aOnly.Anomaly);
        Assert.IsFalse(aOnly.Findings.Any(f => f.Code.StartsWith("anomaly", StringComparison.Ordinal)));
    }

    /// <summary>B的前提缺失明确NG，不能静默跳过：未绑定、模型键不存在、特征实现缺失、ROI尺寸已变。</summary>
    [TestMethod]
    public void MissingPrerequisitesBlock()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("产品A");
        int revision = models.PutAnomalyModel(id, 1, Train(Fixed(false, true, null)));
        var image = Label(1, 4);

        string Codes(InspectionRegion r)
        {
            var result = Run(temp.Store, r, image);
            Assert.AreEqual(ERoiStageState.Failed, result.Execution!.Prerequisites);
            return string.Join(",", result.Findings.Select(f => f.Code));
        }

        StringAssert.Contains(Codes(Fixed(false, true, null)), "anomaly_model_unbound");
        StringAssert.Contains(
            Codes(Fixed(false, true, new AnomalySettings(id, revision, "other"))),
            "anomaly_model_missing"
        );
        StringAssert.Contains(
            Codes(Fixed(false, true, new AnomalySettings(id, revision + 5))),
            "anomaly_model_missing"
        );
        var moved = new InspectionRegion(
            "fixed",
            ERegionKind.Fixed,
            new PixelRect(30, 20, 140, 50),
            anomaly: new AnomalySettings(id, revision)
        ).WithTasks(new RoiInspectionTasks(false, false, true));
        StringAssert.Contains(Codes(moved), "anomaly_model_size_mismatch");

        var cnnEntry = Train(Fixed(false, true, null));
        var foreign = new AnomalyModelEntry(
            "fixed",
            cnnEntry.CopyModel(),
            cnnEntry.Sha256,
            "cnn:0000000000000000@2",
            cnnEntry.Width,
            cnnEntry.Height,
            cnnEntry.LocalRadius,
            cnnEntry.TrainingImages,
            cnnEntry.Threshold,
            cnnEntry.Margin,
            cnnEntry.Stride,
            cnnEntry.MinimumArea
        );
        int next = models.PutAnomalyModel(id, revision, foreign, replaceExisting: true);
        StringAssert.Contains(
            Codes(Fixed(false, true, new AnomalySettings(id, next))),
            "anomaly_feature_unavailable"
        );
    }
}
