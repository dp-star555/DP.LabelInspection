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
/// 方法B逐字符模式：内容自由组合的文字按字符训练（每字多个样本），检测未见过的组合；缺陷定位到具体字符，
/// 没有模型的字符明确NG。
/// </summary>
[TestClass]
public sealed partial class CharacterAnomalyTests
{
    private static readonly PixelRect Line = new PixelRect(10, 12, 300, 64);

    private static ImageFrame Label(string text, int seed, int breakAt = -1)
    {
        using var m = new Mat(90, 320, MatType.CV_8UC1, Scalar.All(235));
        Cv2.Randn(m, Scalar.All(235), Scalar.All(3));
        var origin = new Point(24 + seed % 2, 60);
        Cv2.PutText(m, text, origin, HersheyFonts.HersheySimplex, 1.2, Scalar.All(25), 3);
        if (breakAt >= 0)
        {
            // 在第breakAt个字符的中部横向抹去一条，造成笔画断开。
            int left =
                origin.X
                + Cv2.GetTextSize(
                    text.Substring(0, breakAt),
                    HersheyFonts.HersheySimplex,
                    1.2,
                    3,
                    out _
                ).Width;
            int width = Cv2.GetTextSize(
                text.Substring(breakAt, 1),
                HersheyFonts.HersheySimplex,
                1.2,
                3,
                out _
            ).Width;
            Cv2.Rectangle(m, new Rect(left, 43, width, 5), Scalar.All(235), -1);
        }

        var bytes = new byte[m.Rows * m.Cols];
        Marshal.Copy(m.Data, bytes, 0, bytes.Length);
        return new ImageFrame(m.Cols, m.Rows, EImagePixelFormat.Gray8, bytes);
    }

    private static readonly string[] Good = { "A1B2C3", "3C2B1A", "B3A1C2", "2A3C1B", "C1A2B3" };

    private static CharacterAnomalySample[] Lines()
    {
        var segmenter = new CharacterSegmenter();
        return Good.Select(
                (text, i) =>
                {
                    var image = Label(text, i);
                    var segmentation = segmenter.Segment(image, Line, text);
                    Assert.AreEqual("provisional", segmentation.Status, segmentation.Reason);
                    return new CharacterAnomalySample(image, segmentation.Characters);
                }
            )
            .ToArray();
    }

    private sealed class Temp : IDisposable
    {
        internal readonly string Root = Path.Combine(
            Path.GetTempPath(),
            "dp-character-anomaly-" + Guid.NewGuid().ToString("N")
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

    private static RegionInspectionResult Run(
        InspectionStore store,
        InspectionRegion region,
        ImageFrame image,
        string ocr
    )
    {
        var recognizer = new ScriptedRecognizer();
        recognizer.Set(image, ocr);
        using var backend = new OpenCvInspectionBackend(recognizer, anomalyModels: store.AnomalyLibraries);
        using var engine = new InspectionEngine(backend);
        return engine
            .Inspect(
                new InspectionRequest(
                    image,
                    new InspectionRecipe(
                        "chars",
                        320,
                        90,
                        EInspectionMode.Free,
                        EAlignmentMode.AssumeAligned,
                        new[] { region }
                    )
                )
            )
            .Analysis.Regions.Single();
    }

    private static InspectionRegion Text(AnomalySettings pin, string? expected = null)
    {
        return new InspectionRegion(
            "serial",
            ERegionKind.Text,
            Line,
            true,
            new FieldSettings(expected: expected),
            pin
        ).WithTasks(new RoiInspectionTasks(expected != null, false, true));
    }

    /// <summary>每个字符一个模型，样本来自所有行；字符模型不能用非字母数字键。</summary>
    [TestMethod]
    public void TrainsOneModelPerCharacter()
    {
        var entries = new RegionAnomalyDetector().TrainCharacters(Lines());
        CollectionAssert.AreEqual(
            new[] { "1", "2", "3", "A", "B", "C" },
            entries.Select(e => e.Key).ToArray()
        );
        Assert.IsTrue(entries.All(e => e.Scope == EAnomalyModelScope.Character && e.TrainingImages == 5));
        Assert.IsTrue(entries.All(e => e.Height == entries[0].Height && e.LocalRadius == 1));
        Assert.ThrowsExactly<ArgumentException>(() => entries[0].WithKey("AB"));
    }

    /// <summary>
    /// 只启用B（逐字符）：未见过的组合通过；断开的字被定位到该字；没有模型的字符NG；
    /// 模型与批量发布、配方绑定往返一致。
    /// </summary>
    [TestMethod]
    public void JudgesUnseenCombinationsPerCharacter()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("字体A");
        var entries = new RegionAnomalyDetector().TrainCharacters(Lines());
        int revision = models.PutAnomalyModels(id, 1, entries);
        Assert.AreEqual(2, revision);
        Assert.AreEqual(6, models.LoadAnomalyLibrary(id, revision).Models.Count);
        Assert.IsTrue(
            models
                .LoadAnomalyLibrary(id, revision)
                .Models.Values.All(m => m.Scope == EAnomalyModelScope.Character)
        );
        var pin = new AnomalySettings(id, revision, perCharacter: true);
        var region = Text(pin);
        var back = temp
            .Store.DeserializeRecipe(
                temp.Store.SerializeRecipe(
                    new InspectionRecipe(
                        "r",
                        320,
                        90,
                        EInspectionMode.Free,
                        EAlignmentMode.AssumeAligned,
                        new[] { region }
                    )
                )
            )
            .Regions.Single();
        Assert.IsTrue(back.Anomaly!.PerCharacter);

        var good = Run(temp.Store, region, Label("B1C3A2", 1), "B1C3A2");
        Assert.IsFalse(
            good.Findings.Any(f => f.Verdict != EInspectionVerdict.Ok),
            string.Join(";", good.Findings.Select(f => f.Code + ":" + f.Message))
        );
        Assert.AreEqual(ERoiStageState.Passed, good.Execution!.Quality);
        StringAssert.Contains(
            good.Findings.Single(f => f.Code == "anomaly_summary").Message,
            "6字中6字已检测"
        );
        Assert.AreEqual(good.Anomaly!.Crop.Width, good.Anomaly.HeatMap!.Width);

        var broken = Run(temp.Store, region, Label("B1C3A2", 1, breakAt: 2), "B1C3A2");
        var defects = broken
            .Findings.Where(f => f.Verdict == EInspectionVerdict.Ng && f.Bounds.HasValue)
            .ToArray();
        Assert.IsTrue(
            defects.Length > 0,
            string.Join(";", broken.Findings.Select(f => f.Code + ":" + f.Message))
        );
        Assert.IsTrue(
            defects.All(f => f.Message.StartsWith("字符[C]（第3位）", StringComparison.Ordinal)),
            defects[0].Message
        );
        Assert.AreEqual(ERoiStageState.Failed, broken.Execution!.Quality);

        var unknown = Run(temp.Store, region, Label("B1D3A2", 1), "B1D3A2");
        var missing = unknown.Findings.Single(f => f.Code == "anomaly_character_model_missing");
        StringAssert.Contains(missing.Message, "字符[D]（第3位）");
    }

    /// <summary>已知必需字符缺少模型时在前提检查阶段阻断；库中只有整ROI模型时逐字符模式阻断。</summary>
    [TestMethod]
    public void PrerequisitesBlockMissingCharacters()
    {
        using var temp = new Temp();
        var models = temp.Store.AnomalyLibraries;
        string id = models.CreateAnomalyLibrary("字体A");
        int revision = models.PutAnomalyModels(id, 1, new RegionAnomalyDetector().TrainCharacters(Lines()));
        var image = Label("B1D3A2", 1);
        var known = Run(
            temp.Store,
            Text(new AnomalySettings(id, revision, perCharacter: true), "B1D3A2"),
            image,
            "B1D3A2"
        );
        Assert.AreEqual(ERoiStageState.Failed, known.Execution!.Prerequisites);
        StringAssert.Contains(
            known.Findings.Single(f => f.Code == "anomaly_character_model_missing").Message,
            "D"
        );

        // 文字行绑定到字符模型库但没选逐字符：提示改为逐字符检查，而不是只说缺模型。
        var whole = Run(temp.Store, Text(new AnomalySettings(id, revision)), image, "B1D3A2");
        var hint = whole.Findings.Single(f => f.Code == "anomaly_model_missing").Message;
        StringAssert.Contains(hint, "逐字符检查");
        StringAssert.Contains(hint, "123ABC");

        string empty = models.CreateAnomalyLibrary("空库");
        var none = Run(temp.Store, Text(new AnomalySettings(empty, 1, perCharacter: true)), image, "B1D3A2");
        StringAssert.Contains(string.Join(",", none.Findings.Select(f => f.Code)), "anomaly_model_missing");
    }
}
