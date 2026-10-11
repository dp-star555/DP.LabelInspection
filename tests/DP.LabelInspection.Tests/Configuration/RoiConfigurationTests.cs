using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Tests;

/// <summary>新检测项目是唯一配置依据，并覆盖旧配方迁移。</summary>
[TestClass]
public sealed class RoiConfigurationTests
{
    /// <summary>改绑字库修订只替换字库绑定，保留范围、其余规则和明确选择的检测项目。</summary>
    [TestMethod]
    public void WithFieldRebindsLibraryRevisionAndKeepsTasks()
    {
        var field = new FieldSettings("lib", 3, expected: "A01", maximumDifference: .08, minimumConfidence: .6);
        var region = new InspectionRegion("ROI-1", ERegionKind.Text, new PixelRect(1, 2, 30, 10), true, field)
            .WithTasks(new RoiInspectionTasks(false, true));
        var rebound = region.WithField(region.Field.WithLibrary("lib", 4));
        Assert.AreEqual(4, rebound.Field.LibraryRevision);
        Assert.AreEqual("lib", rebound.Field.LibraryId);
        Assert.AreEqual("A01", rebound.Field.Expected);
        Assert.AreEqual(.08, rebound.Field.MaximumDifference);
        Assert.AreEqual(.6, rebound.Field.MinimumConfidence);
        Assert.AreEqual(region.Bounds, rebound.Bounds);
        Assert.IsTrue(rebound.SingleLine);
        Assert.IsFalse(rebound.Tasks.ReadData);
        Assert.IsTrue(rebound.Tasks.CheckQuality);
        Assert.ThrowsExactly<ArgumentException>(() => region.Field.WithLibrary("lib", null));
    }

    /// <summary>显式项目覆盖冲突的历史标志，不改变阈值或源对象。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void QualityProjectSynchronizesLegacyOptions(bool selected)
    {
        var original = new InspectionRegion(
            "code",
            ERegionKind.Barcode,
            new PixelRect(0, 0, 32, 32),
            field: new FieldSettings(
                barcodeType: EBarcodeKind.OneDimensional,
                barcodePrint: new BarcodePrintOptions(!selected, 11, .12, 2, true, true, .33)
            )
        );
        var changed = original.WithTasks(new RoiInspectionTasks(false, selected));
        Assert.AreEqual(selected, changed.Field.BarcodePrint.Enabled);
        Assert.AreEqual(!selected, original.Field.BarcodePrint.Enabled);
        Assert.IsFalse(changed.Tasks.ReadData);
        Assert.AreEqual(11, changed.Field.BarcodePrint.MinimumArea);
        Assert.AreEqual(.12, changed.Field.BarcodePrint.MinimumFraction);
        Assert.AreEqual(2, changed.Field.BarcodePrint.EdgeTolerance);
        Assert.AreEqual(.33, changed.Field.BarcodePrint.MinimumInkLoss);
        Assert.IsTrue(changed.Field.BarcodePrint.CheckQrQuietZone);
    }

    /// <summary>加载冲突JSON时显式Tasks优先，缺少Tasks时迁移旧标志。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void JsonUsesOneQualityAuthority(bool selected)
    {
        string root = Path.Combine(Path.GetTempPath(), "dp-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InspectionStore(root, new OpenCvImageCodec());
            var roi = new InspectionRegion(
                "code",
                ERegionKind.Barcode,
                new PixelRect(0, 0, 32, 32)
            ).WithTasks(new RoiInspectionTasks(false, selected));
            var recipe = new InspectionRecipe(
                "config",
                32,
                32,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            );
            var json = JObject.Parse(store.SerializeRecipe(recipe));
            var serialized = (JObject)json["regions"]![0]!;
            serialized["field"]!["barcodePrint"]!["enabled"] = !selected;
            var loaded = store.DeserializeRecipe(json.ToString()).Regions.Single();
            Assert.AreEqual(selected, loaded.Tasks.CheckQuality);
            Assert.AreEqual(selected, loaded.Field.BarcodePrint.Enabled);
            Assert.IsFalse(loaded.Tasks.ReadData);
            serialized.Remove("tasks");
            var legacy = store.DeserializeRecipe(json.ToString()).Regions.Single();
            Assert.AreEqual(!selected, legacy.Tasks.CheckQuality);
            Assert.AreEqual(!selected, legacy.Field.BarcodePrint.Enabled);
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
