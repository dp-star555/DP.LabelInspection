using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

/// <summary>真实分割、异常训练、固定版本存储及正式检测共同支持Unicode单字。</summary>
[TestClass]
public sealed class UnicodeCharacterAnomalyTests
{
    /// <summary>中文、标点、斜杠及代理对模型键无歧义，保留历史ASCII键。</summary>
    [TestMethod]
    public void UnicodeModelKeysRoundTripWithoutSplittingSurrogatesOrSlash()
    {
        foreach (string label in new[] { "中", "：", "/", "𠮷", "A", "1" })
        foreach (string? group in new[] { null, "字体" })
        {
            string key = AnomalyModelEntry.CharacterKey(group, label);
            var entry = new AnomalyModelEntry(
                key,
                new byte[] { 1 },
                new string('a', 64),
                "handcrafted",
                32,
                61,
                1,
                2,
                1,
                0,
                1,
                1,
                scope: EAnomalyModelScope.Character
            );
            Assert.AreEqual(label, entry.Character);
            Assert.AreEqual(group, entry.Group);
            Assert.AreEqual(label, entry.WithKey(AnomalyModelEntry.CharacterKey(null, label)).Character);
        }
        foreach (string label in new[] { "", " ", "a\u0301", "\n", "\ud840", "AB" })
            Assert.ThrowsExactly<ArgumentException>(() => AnomalyModelEntry.CharacterKey("font", label));
    }

    /// <summary>实际中文字体和标点完整训练；保存/导入不改键，B正式检查良品通过、缺墨明确NG。</summary>
    [TestMethod]
    public async Task RealChineseLineTrainsPublishesAndDetectsMissingPunctuationInk()
    {
        const string text = "中文标签：100，合格。";
        string root = Path.Combine(Path.GetTempPath(), "unicode-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InspectionStore(root, new OpenCvImageCodec());
            var frame = new OpenCvImageCodec().Decode(
                File.ReadAllBytes(
                    Path.Combine(AppContext.BaseDirectory, "Text", "Fixtures", "glyph-unicode-line.png")
                )
            );
            var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
            var roi = new InspectionRegion(
                "文字",
                ERegionKind.Text,
                bounds,
                true,
                new FieldSettings(expected: text)
            );
            var session = new AnomalyTrainingSession();
            var model = session.AddModel("文字", EAnomalyTrainingKind.Characters, roi);
            using var extractingBackend = new OpenCvInspectionBackend();
            using var extracting = new InspectionEngine(extractingBackend);
            for (int i = 0; i < 3; i++)
            {
                var image = session.AddImage(
                    new PixelSnapshot(frame.Width, frame.Height, frame.Format, frame.CopyPixels()),
                    "good" + i
                );
                var sample = session.AddSample(image, model, bounds);
                session.SetConfirmedText(sample, text);
            }
            await session.ExtractAsync(extracting);
            Assert.AreEqual(0, session.Problems().Count, string.Join(";", session.Problems()));
            Assert.AreEqual(11, session.CharacterCoverage().Count);
            var entries = session.Train(new RegionAnomalyDetector());
            Assert.AreEqual(11, entries.Count);
            Assert.IsTrue(entries.All(e => e.Normalization == A.ECharacterNormalization.LineRegion));
            Assert.IsTrue(entries.Any(e => e.Character == "：") && entries.Any(e => e.Character == "中"));
            string id = store.AnomalyLibraries.CreateAnomalyLibrary("Unicode-B");
            store.AnomalyLibraries.PutAnomalyModels(id, 1, entries);
            Assert.AreEqual(
                AnomalyLibraryStore.UnicodeSchema,
                (string?)store.AnomalyLibraries.Read(id, 2)["schema"]
            );
            Assert.AreEqual(
                AnomalyLibraryStore.Schema,
                (string?)store.AnomalyLibraries.Read(id, 1)["schema"]
            );
            string imported = store.AnomalyLibraries.ImportAnomalyLibrary(
                store.AnomalyLibraries.ExportAnomalyLibrary(id, 2)
            );
            CollectionAssert.AreEquivalent(
                entries.Select(e => e.Key).ToArray(),
                store.AnomalyLibraries.LoadAnomalyLibrary(imported, 1).Models.Keys.ToArray()
            );
            var bound = session.Bindings(id, 2).Single().WithTasks(new RoiInspectionTasks(true, false, true));
            var recipe = new InspectionRecipe(
                "Unicode B",
                frame.Width,
                frame.Height,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { bound }
            );
            using var backend = new OpenCvInspectionBackend(
                new Recognizer(text),
                anomalyModels: store.AnomalyLibraries
            );
            using var engine = new InspectionEngine(backend);
            var good = engine.Inspect(TestRequests.FromSnapshot(frame, recipe));
            Assert.AreEqual(
                EInspectionVerdict.Ok,
                good.Verdict,
                string.Join(";", good.Findings.Select(f => f.Message))
            );
            StringAssert.Contains(
                good.Analysis.Regions[0].Findings.Single(f => f.Code == "anomaly_summary").Message,
                "12字中12字已检测"
            );
            var colon = session.Samples[0].Segmentation!.Characters.Single(c => c.Character == "：").Bounds;
            var pixels = frame.CopyPixels();
            int channels = frame.Format == EImagePixelFormat.Gray8 ? 1 : 3;
            for (int y = colon.Y; y < colon.Y + colon.Height / 2; y++)
            for (int x = colon.X; x < colon.X + colon.Width; x++)
            for (int channel = 0; channel < channels; channel++)
                pixels[(y * frame.Width + x) * channels + channel] = 255;
            var damaged = new PixelSnapshot(frame.Width, frame.Height, frame.Format, pixels);
            // 直接使用原始身份和完整分割单元，排除OCR/重新切字代替异常检查的假阳性。
            var detector = new CharacterAnomalyDetector();
            var models = entries.ToDictionary(
                e => e.Character!,
                e => new CharacterAnomalyModel(
                    e,
                    A.PatchAnomalyModel.FromBytes(e.CopyModel()),
                    new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector()
                )
            );
            using var source = AlgorithmContractAdapter.ToVision(damaged);
            var inspected = detector.Inspect(
                source,
                session.Samples[0].Segmentation!.Characters,
                bounds,
                label => models.TryGetValue(label, out var value) ? value : null
            );
            Assert.AreEqual(12, inspected.Scores.Count);
            Assert.IsTrue(inspected.Scores.Any(s => s.Character == "：" && !s.Passed));
            var bad = engine.Inspect(TestRequests.FromSnapshot(damaged, recipe));
            Assert.AreEqual(EInspectionVerdict.Ng, bad.Verdict);
            // 删除中文模型必须NG，不能只比较ASCII然后声称整行通过。
            store.AnomalyLibraries.RemoveAnomalyModel(id, 2, AnomalyModelEntry.CharacterKey("文字", "中"));
            var missingRoi = bound.WithAnomaly(new AnomalySettings(id, 3, "文字", perCharacter: true));
            var missing = engine.Inspect(
                TestRequests.FromSnapshot(
                    frame,
                    new InspectionRecipe(
                        "missing",
                        frame.Width,
                        frame.Height,
                        EInspectionMode.Free,
                        EAlignmentMode.AssumeAligned,
                        new[] { missingRoi }
                    )
                )
            );
            Assert.AreEqual(EInspectionVerdict.Ng, missing.Verdict);
            Assert.IsTrue(
                missing.Analysis.Regions[0].Findings.Any(f => f.Code == "anomaly_character_model_missing")
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>纯标点不依赖ASCII锚点，ROI模式保持点的位置；补充平面汉字与斜杠也真正参与训练和检测。</summary>
    [TestMethod]
    public void PunctuationOnlyAndSupplementaryGlyphsTrainUsingStableRoi()
    {
        foreach (var labels in new[] { new[] { "：", "/" }, new[] { "：", "/", "𠮷" } })
        {
            var pixels = Enumerable.Repeat((byte)255, 128 * 64).ToArray();
            void Ink(int x, int y, int width, int height)
            {
                for (int yy = y; yy < y + height; yy++)
                for (int xx = x; xx < x + width; xx++)
                    pixels[yy * 128 + xx] = 20;
            }
            Ink(17, 18, 6, 6);
            Ink(17, 36, 6, 6);
            for (int y = 16; y < 46; y++)
                Ink(53 + (46 - y) / 3, y, 3, 1);
            Ink(87, 18, 22, 3);
            Ink(96, 16, 3, 30);
            Ink(87, 38, 22, 3);
            var frame = new PixelSnapshot(128, 64, EImagePixelFormat.Gray8, pixels);
            var bounds = new PixelRect(0, 0, 128, 64);
            var patches = labels
                .Select(
                    (label, i) =>
                    {
                        var b = new PixelRect(i * 40 + 8, 12, 28, 36);
                        return new CharacterPatch(label, i, b, frame.Crop(b));
                    }
                )
                .ToArray();
            var lines = Enumerable
                .Range(0, 3)
                .Select(_ => new CharacterAnomalySample(
                    new PixelSnapshot(128, 64, EImagePixelFormat.Gray8, frame.CopyPixels()),
                    patches,
                    lineBounds: bounds
                ))
                .ToArray();
            var detector = new CharacterAnomalyDetector();
            var entries = detector.Train(lines);
            Assert.AreEqual(labels.Length, entries.Count);
            var models = entries.ToDictionary(
                e => e.Character!,
                e => new CharacterAnomalyModel(
                    e,
                    A.PatchAnomalyModel.FromBytes(e.CopyModel()),
                    new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector()
                )
            );
            using var source = AlgorithmContractAdapter.ToVision(frame);
            var good = detector.Inspect(source, patches, bounds, label => models[label]);
            Assert.AreEqual(labels.Length, good.Scores.Count);
            Assert.IsTrue(good.Scores.All(s => s.Passed));
            for (int y = 18; y < 24; y++)
            for (int x = 17; x < 23; x++)
                pixels[y * 128 + x] = 255;
            using var damaged = AlgorithmContractAdapter.ToVision(
                new PixelSnapshot(128, 64, EImagePixelFormat.Gray8, pixels)
            );
            var bad = detector.Inspect(damaged, patches, bounds, label => models[label]);
            Assert.IsTrue(
                bad.Scores.Single(s => s.Character == "：")
                    .Findings.Any(f => f.Verdict == EInspectionVerdict.Ng)
            );
        }
    }

    /// <summary>未知归一化方式不能导入；旧ASCII文档缺字段仍采用旧方式。</summary>
    [TestMethod]
    public void NormalizationMetadataIsValidatedAndLegacyDefaultsAreStable()
    {
        string root = Path.Combine(Path.GetTempPath(), "b-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AnomalyLibraryStore(root);
            var bytes = new byte[] { 1, 2, 3 };
            using var sha = System.Security.Cryptography.SHA256.Create();
            string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            var entry = new AnomalyModelEntry(
                "A",
                bytes,
                hash,
                "handcrafted",
                32,
                61,
                1,
                2,
                1,
                0,
                1,
                1,
                scope: EAnomalyModelScope.Character
            );
            string id = store.CreateAnomalyLibrary("legacy");
            store.PutAnomalyModel(id, 1, entry);
            var doc = Newtonsoft.Json.Linq.JObject.Parse(store.ExportAnomalyLibrary(id, 2));
            ((Newtonsoft.Json.Linq.JObject)doc["models"]!["A"]!).Remove("normalization");
            string imported = store.ImportAnomalyLibrary(doc.ToString());
            Assert.AreEqual(
                A.ECharacterNormalization.LineInk,
                store.LoadAnomalyLibrary(imported, 1).Models["A"].Normalization
            );
            doc["schema"] = AnomalyLibraryStore.UnicodeSchema;
            doc["models"]!["A"]!["normalization"] = 999;
            Assert.ThrowsExactly<InvalidDataException>(() => store.ImportAnomalyLibrary(doc.ToString()));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>.NET48下目标模型路径可用时，原子写入临时名不能因追加SHA/GUID超过MAX_PATH而失败。</summary>
    [TestMethod]
    public void ModelPublicationWithinLegacyPathBudgetUsesShortSiblingTemporaryFile()
    {
        var bytes = new byte[] { 7, 8, 9 };
        using var sha = System.Security.Cryptography.SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        string suffix = Path.Combine(
            "anomaly-libraries",
            "anomaly-" + new string('a', 32),
            "models",
            hash + ".bin"
        );
        string prefix = Path.Combine(Path.GetTempPath(), "b-long-" + Guid.NewGuid().ToString("N"));
        int padding = 240 - suffix.Length - prefix.Length - 2;
        string root = Path.Combine(prefix, new string('x', Math.Max(1, padding)));
        try
        {
            var store = new AnomalyLibraryStore(root);
            string id = store.CreateAnomalyLibrary("long root");
            var entry = new AnomalyModelEntry(
                "A",
                bytes,
                hash,
                "handcrafted",
                32,
                61,
                1,
                2,
                1,
                0,
                1,
                1,
                scope: EAnomalyModelScope.Character
            );
            Assert.AreEqual(2, store.PutAnomalyModel(id, 1, entry));
            CollectionAssert.AreEqual(bytes, store.LoadAnomalyLibrary(id, 2).Models["A"].CopyModel());
            Assert.IsFalse(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(prefix))
                Directory.Delete(prefix, true);
        }
    }

    private sealed class Recognizer : A.ITextLineRecognizer
    {
        private readonly string _text;

        internal Recognizer(string text) => _text = text;

        public A.TextLineRecognition Recognize(
            DP.Vision.IImageSource source,
            PixelRect bounds,
            CancellationToken token = default
        )
        {
            A.CharacterIdentity.TryTokenizeLine(_text, out var labels);
            return new A.TextLineRecognition(
                bounds,
                "fixture",
                320,
                48,
                labels.Select((_, i) => new A.CtcStep(i + 1, .99f)),
                labels.Select((label, i) => new A.CtcToken(label, i, i + 1, .99f))
            );
        }

        public void Dispose() { }
    }
}
