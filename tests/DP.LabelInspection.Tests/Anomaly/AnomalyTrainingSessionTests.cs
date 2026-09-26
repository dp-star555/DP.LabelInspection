using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 批量训练采集：多图、每图多个样本框、一次训练全部模型并作为一个版本发布；内容固定模型的框统一尺寸并自动对齐。
/// </summary>
[TestClass]
public sealed partial class AnomalyTrainingSessionTests
{
    /// <summary>标签图：左上图形（位置按dx/dy偏移）、中部一行文字、右侧随机条纹“码”。</summary>
    private static ImageFrame Label(int dx, int dy, string text, int seed, bool logoDefect = false)
    {
        using var m = new Mat(160, 420, MatType.CV_8UC1, Scalar.All(235));
        Cv2.Randn(m, Scalar.All(235), Scalar.All(3));
        var logo = new Rect(30 + dx, 20 + dy, 70, 40);
        Cv2.Rectangle(m, logo, Scalar.All(30), 3);
        Cv2.Line(m, new Point(logo.X, logo.Y), new Point(logo.Right, logo.Bottom), Scalar.All(30), 3);
        Cv2.Circle(m, new Point(logo.X + 50, logo.Y + 14), 7, Scalar.All(30), -1);
        if (logoDefect)
        {
            Cv2.Rectangle(m, new Rect(logo.X + 10, logo.Y - 2, 12, 6), Scalar.All(235), -1);
        }

        Cv2.PutText(m, text, new Point(20, 125), HersheyFonts.HersheySimplex, 1.1, Scalar.All(25), 3);
        var rnd = new Random(seed);
        for (int x = 300; x < 400; x += rnd.Next(3, 8))
        {
            Cv2.Rectangle(m, new Rect(x, 20, rnd.Next(1, 4), 60), Scalar.All(30), -1);
        }

        var bytes = new byte[m.Rows * m.Cols];
        Marshal.Copy(m.Data, bytes, 0, bytes.Length);
        return new ImageFrame(m.Cols, m.Rows, EImagePixelFormat.Gray8, bytes);
    }

    private static readonly string[] Texts = { "A1B2C3", "3C2B1A", "B3A1C2", "2A3C1B" };

    private static readonly PixelRect TextLine = new PixelRect(10, 90, 300, 55);

    /// <summary>内容固定：第一个框定尺寸；之后粗画的框自动改为同尺寸并对齐到同一内容；改尺寸对全部样本生效。</summary>
    [TestMethod]
    public void FixedContentBoxesShareSizeAndSnap()
    {
        var session = new AnomalyTrainingSession();
        var a = session.AddImage(Label(0, 0, "A1", 1), "a");
        var b = session.AddImage(Label(7, -5, "A1", 2), "b");
        var logo = session.AddModel("标志", EAnomalyTrainingKind.FixedContent);
        var first = session.AddSample(a, logo, new PixelRect(22, 12, 86, 56));
        Assert.AreEqual(86, logo.Width);
        Assert.AreEqual(new PixelRect(22, 12, 86, 56), first.Bounds);

        // 在b上粗画：尺寸不同、偏了几像素。
        var second = session.AddSample(b, logo, new PixelRect(24, 10, 100, 70));
        Assert.AreEqual(new PixelRect(22 + 7, 12 - 5, 86, 56), second.Bounds);

        session.MoveSample(second, new PixelRect(second.Bounds.X - 2, second.Bounds.Y - 2, 90, 60));
        Assert.AreEqual(90, logo.Width);
        Assert.IsTrue(session.Of(logo).All(s => s.Bounds.Width == 90 && s.Bounds.Height == 60));
        Assert.AreEqual(new PixelRect(20, 10, 90, 60), first.Bounds);

        var variable = session.AddModel("码", EAnomalyTrainingKind.VariableContent);
        var free = session.AddSample(a, variable, new PixelRect(290, 10, 120, 80));
        Assert.AreEqual(new PixelRect(290, 10, 120, 80), free.Bounds);
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.AddSample(a, variable, new PixelRect(400, 10, 40, 40))
        );
    }

    /// <summary>
    /// 多图、每图多个框：内容固定、内容可变和逐字符三种模型一次训练，作为一个版本发布；绑定后配方ROI启用B，
    /// 内容固定模型改为样本尺寸；用发布的模型检测，良品通过、图形缺口NG。
    /// </summary>
    [TestMethod]
    public async Task TrainsAllModelsAsOneRevision()
    {
        var recipe = new[]
        {
            new InspectionRegion("标志", ERegionKind.Fixed, new PixelRect(25, 15, 80, 50)),
            new InspectionRegion("序列号", ERegionKind.Text, TextLine, true),
            new InspectionRegion("码", ERegionKind.Barcode, new PixelRect(292, 12, 116, 76)),
        };
        var session = new AnomalyTrainingSession();
        var models = session.ImportRecipe(recipe);
        CollectionAssert.AreEqual(
            new[]
            {
                EAnomalyTrainingKind.FixedContent,
                EAnomalyTrainingKind.Characters,
                EAnomalyTrainingKind.VariableContent,
            },
            models.Select(m => m.Kind).ToArray()
        );
        for (int i = 0; i < Texts.Length; i++)
        {
            var image = session.AddImage(Label(i % 2, i % 3 - 1, Texts[i], i + 1), "good-" + i);
            var placed = session.PlaceRecipe(image);
            Assert.AreEqual(3, placed.Count);
            session.SetConfirmedText(
                placed.Single(s => s.Model.Kind == EAnomalyTrainingKind.Characters),
                Texts[i]
            );
        }

        Assert.AreEqual(4, session.Problems().Count(p => p.Contains("尚未提取")));
        var service = new Candidates();
        Assert.AreEqual(4, await session.ExtractAsync(service));
        Assert.AreEqual(0, session.Problems().Count);
        Assert.IsTrue(session.CharacterCoverage().All(c => c.Value == 4));
        Assert.AreEqual(6, session.CharacterCoverage().Count);

        // 人工取消一个字符、修改一个身份（改回原值，验证接口）。
        var line = session.Samples.First(s => s.Model.Kind == EAnomalyTrainingKind.Characters);
        session.SetInclude(line, 0, false);
        session.SetLabel(line, 1, line.Labels[1]);
        Assert.AreEqual(3, session.CharacterCoverage().Single(c => c.Key == "序列号/A").Value);

        var entries = session.Train(new RegionAnomalyDetector());
        CollectionAssert.AreEquivalent(
            new[] { "标志", "码", "序列号/1", "序列号/2", "序列号/3", "序列号/A", "序列号/B", "序列号/C" },
            entries.Select(e => e.Key).ToArray()
        );
        Assert.AreEqual(3, entries.Single(e => e.Key == "标志").LocalRadius);
        Assert.AreEqual(0, entries.Single(e => e.Key == "码").LocalRadius);

        var store = new AnomalyLibraryStore(
            Path.Combine(Path.GetTempPath(), "dp-batch-" + Guid.NewGuid().ToString("N"))
        );
        string id = store.CreateAnomalyLibrary("批量");
        int revision = store.PutAnomalyModels(id, 1, entries);
        Assert.AreEqual(2, revision);

        var bound = session.Bindings(id, revision);
        Assert.AreEqual(3, bound.Count);
        Assert.IsTrue(bound.All(r => r.Tasks.DetectAnomaly && r.Anomaly!.LibraryRevision == 2));
        Assert.IsTrue(bound.Single(r => r.Name == "序列号").Anomaly!.PerCharacter);
        Assert.AreEqual("序列号", bound.Single(r => r.Name == "序列号").Anomaly!.ModelKey);
        Assert.AreEqual(new PixelRect(25, 15, 80, 50), bound.Single(r => r.Name == "标志").Bounds);

        var fixedRoi = bound
            .Single(r => r.Name == "标志")
            .WithTasks(new RoiInspectionTasks(false, false, true));
        RegionInspectionResult Run(ImageFrame image)
        {
            using var backend = new OpenCvInspectionBackend(anomalyModels: store);
            using var engine = new InspectionEngine(backend);
            return engine
                .Inspect(
                    new InspectionRequest(
                        image,
                        new InspectionRecipe(
                            "batch",
                            420,
                            160,
                            EInspectionMode.Free,
                            EAlignmentMode.AssumeAligned,
                            new[] { fixedRoi }
                        )
                    )
                )
                .Analysis.Regions.Single();
        }

        var good = Run(Label(0, 0, "C1", 9));
        Assert.IsFalse(
            good.Findings.Any(f => f.Verdict != EInspectionVerdict.Ok),
            string.Join(";", good.Findings.Select(f => f.Code + ":" + f.Message))
        );
        var bad = Run(Label(0, 0, "C1", 9, logoDefect: true));
        Assert.IsTrue(bad.Findings.Any(f => f.Code == "patch_anomaly" || f.Verdict == EInspectionVerdict.Ng));
    }

    /// <summary>
    /// 训练页保留期间配方变化：新画的ROI自动加入模型列表，已有模型关联到最新ROI配置，配方删除的ROI模型转为独立模型；
    /// 人工删除的模型不再自动加回，但手动“从配方导入”可以加回。
    /// </summary>
    [TestMethod]
    public void SyncRecipeFollowsRecipeChanges()
    {
        var session = new AnomalyTrainingSession();
        var logo = new InspectionRegion("标志", ERegionKind.Fixed, new PixelRect(25, 15, 80, 50));
        var old = new InspectionRegion("旧码", ERegionKind.Barcode, new PixelRect(292, 12, 116, 76));
        session.SyncRecipe(new[] { logo, old });
        Assert.AreEqual(2, session.Models.Count);

        var moved = logo.WithBounds(new PixelRect(30, 15, 80, 50));
        var line = new InspectionRegion("ROI-4", ERegionKind.Text, TextLine, true);
        var added = session.SyncRecipe(new[] { moved, line });
        Assert.AreEqual("ROI-4", added.Single().Name);
        Assert.AreEqual(EAnomalyTrainingKind.Characters, added.Single().Kind);
        Assert.AreSame(moved, session.Models.Single(m => m.Name == "标志").Region);
        Assert.IsNull(session.Models.Single(m => m.Name == "旧码").Region);

        session.RemoveModel(session.Models.Single(m => m.Name == "ROI-4"));
        Assert.AreEqual(0, session.SyncRecipe(new[] { moved, line }).Count);
        Assert.AreEqual(1, session.ImportRecipe(new[] { moved, line }).Count);
    }

    /// <summary>采集可保存后再打开：图像（无路径的另存为PNG）、模型尺寸、样本框、逐字符确认文本与取消的字符都恢复。</summary>
    [TestMethod]
    public async Task ProjectRoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dp-batch-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var codec = new OpenCvImageCodec();
            var recipe = new[] { new InspectionRegion("序列号", ERegionKind.Text, TextLine, true) };
            var session = new AnomalyTrainingSession();
            session.ImportRecipe(recipe);
            var logo = session.AddModel("标志", EAnomalyTrainingKind.FixedContent);
            var image = session.AddImage(Label(0, 0, Texts[0], 1), "当前图像");
            session.AddSample(image, logo, new PixelRect(22, 12, 86, 56));
            var line = session.AddSample(image, session.Models[0], TextLine);
            session.SetConfirmedText(line, Texts[0]);
            await session.ExtractAsync(new Candidates());
            session.SetLabel(line, 2, "8");
            session.SetInclude(line, 4, false);
            session.SetGroup(session.Models[0], "字体A");
            string file = Path.Combine(dir, "采集.json");
            AnomalyTrainingProject.Save(session, file, codec);

            var back = AnomalyTrainingProject.Load(file, codec, recipe, out var excluded);
            Assert.AreEqual(1, back.Images.Count);
            Assert.IsTrue(File.Exists(back.Images[0].Path));
            Assert.AreEqual(86, back.Models.Single(m => m.Name == "标志").Width);
            Assert.AreSame(recipe[0], back.Models.Single(m => m.Name == "序列号").Region);
            Assert.AreEqual("字体A", back.Models.Single(m => m.Name == "序列号").CharacterGroup);
            var restored = back.Samples.Single(s => s.Model.Kind == EAnomalyTrainingKind.Characters);
            Assert.AreEqual("A182C3", restored.ConfirmedText);
            CollectionAssert.AreEqual(new[] { 4 }, excluded[restored]);
            Assert.AreEqual(
                new PixelRect(22, 12, 86, 56),
                back.Samples.Single(s => s.Model.Name == "标志").Bounds
            );
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
