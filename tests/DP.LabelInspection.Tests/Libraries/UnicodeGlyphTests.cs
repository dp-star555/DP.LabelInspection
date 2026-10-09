using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

/// <summary>中文、标点与补充平面字形贯穿制库、固定修订与真实OpenCV质量链路。</summary>
[TestClass]
public sealed class UnicodeGlyphTests
{
    private static PixelSnapshot Blocks(int count = 3)
    {
        int width = count * 24;
        var pixels = Enumerable.Repeat((byte)255, width * 32).ToArray();
        for (int i = 0; i < count; i++)
        for (int y = 6; y < 26; y++)
        for (int x = i * 24 + 6; x < i * 24 + 18; x++)
            pixels[y * width + x] = 0;
        return new PixelSnapshot(width, 32, EImagePixelFormat.Gray8, pixels);
    }

    /// <summary>独立Unicode单字涵盖汉字、标点、符号和补充平面，不限制为ASCII。</summary>
    /// <param name="label">保持原始编码的单字身份。</param>
    [TestMethod]
    [DataRow("中")]
    [DataRow("文")]
    [DataRow("：")]
    [DataRow(":")]
    [DataRow("。")]
    [DataRow("/")]
    [DataRow("\"")]
    [DataRow("℃")]
    [DataRow("𠮷")]
    [DataRow("A")]
    public void SinglePrintableGlyphIsAccepted(string label)
    {
        var reference = new GlyphReference(label, Blocks(1), "hash");
        var patch = new CharacterPatch(label, 0, new PixelRect(0, 0, 24, 32), Blocks(1));
        Assert.AreEqual(label, reference.Character);
        Assert.AreEqual(label, patch.Character);
    }

    /// <summary>不可见、多标量、组合序列与损坏UTF-16不能作为独立字形。</summary>
    [TestMethod]
    public void InvalidLabelsAreRejectedWithoutNormalization()
    {
        foreach (
            var label in new[]
            {
                "",
                "中文",
                " ",
                "\u3000",
                "\n",
                "\u200b",
                "\u0301",
                "e\u0301",
                "\ud800",
                "\udc00",
            }
        )
            Assert.ThrowsExactly<ArgumentException>(() => new GlyphImportItem(label, Blocks(1)));
    }

    /// <summary>超过原62字限制仍可往返，保留特殊JSON键、大小写和历史像素。</summary>
    [TestMethod]
    public void MoreThanSixtyTwoGlyphsRoundTripWithHistoryAndExactKeys()
    {
        using var data = new Store();
        string id = data.Value.CreateLibrary("中文标点");
        var labels = Enumerable
            .Range(0, 70)
            .Select(i => ((char)(0x4e00 + i)).ToString())
            .Concat(new[] { ":", "：", "/", "\"", "𠮷", "A", "a" })
            .ToArray();
        int revision = data.Value.PutGlyphs(id, 1, labels.Select(c => new GlyphImportItem(c, Blocks(1))));
        Assert.AreEqual(2, revision);
        Assert.AreEqual(0, data.Value.Load(id, 1).Glyphs.Count);
        var snapshot = data.Value.Load(id, revision);
        CollectionAssert.AreEquivalent(labels, snapshot.Glyphs.Keys.ToArray());
        var imported = data.Value.ImportLibrary(data.Value.ExportLibrary(id, revision));
        CollectionAssert.AreEquivalent(labels, data.Value.Load(imported, 1).Glyphs.Keys.ToArray());
        data.Value.RemoveGlyph(id, revision, "/");
        Assert.IsTrue(data.Value.Load(id, revision).Glyphs.ContainsKey("/"));
        Assert.IsFalse(data.Value.Load(id, revision + 1).Glyphs.ContainsKey("/"));
    }

    /// <summary>跨图暂存支持中文标点，后续标签编辑不暗改已确认条目。</summary>
    [TestMethod]
    public void DraftStagesChinesePunctuationAndSupplementaryGlyphs()
    {
        using var data = new Store();
        string id = data.Value.CreateLibrary("font");
        var draft = new GlyphDraftSession();
        draft.LoadImage(Blocks());
        string[] labels = { "中", "：", "𠮷" };
        var ids = labels.Select((c, i) => draft.AddManual(new PixelRect(i * 24, 0, 24, 32), c)).ToArray();
        draft.Stage(id, ids, Array.Empty<string>());
        draft.SetLabel(ids[0], "文");
        Assert.AreEqual("中", draft.Pending[0].Character);
        Assert.AreEqual(2, draft.Publish(data.Value, id, 1));
        CollectionAssert.AreEquivalent(labels, data.Value.Load(id, 2).Glyphs.Keys.ToArray());
    }

    /// <summary>正式候选提取保留标点，空格和代理对不会错位字符索引。</summary>
    [TestMethod]
    public async Task ManualExtractionKeepsPunctuationAndScalarTokenIndices()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var source = AlgorithmContractAdapter.ToVision(Blocks());
        var result = await engine.ExtractGlyphCandidatesAsync(source, new PixelRect(0, 0, 72, 32), "中 : 𠮷");
        CollectionAssert.AreEqual(
            new[] { "中", ":", "𠮷" },
            result.Segmentation.Characters.Select(c => c.Character).ToArray()
        );
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2 },
            result.Segmentation.Characters.Select(c => c.TokenIndex).ToArray()
        );
    }

    /// <summary>等格与字表按Unicode单字数切格，不拆开补充平面汉字。</summary>
    [TestMethod]
    public void EqualCellsAndSheetCountGlyphsNotUtf16Units()
    {
        using var data = new Store();
        string id = data.Value.CreateLibrary("sheet");
        var frame = Blocks();
        using var source = AlgorithmContractAdapter.ToVision(frame);
        using var cells = new DP.Vision.OpenCv.OpenCvCharacterSegmenter().EqualCells(
            source,
            new A.PixelBounds(0, 0, 72, 32),
            "中:𠮷"
        );
        Assert.AreEqual(3, cells.Characters.Count);
        Assert.AreEqual(24, cells.Characters[2].Bounds.Width);
        Assert.AreEqual("𠮷", cells.Characters[2].Character);
        Assert.IsTrue(new FieldSettings(expected: "中:𠮷", equalCells: true).EqualCells);
        data.Value.PutSheet(id, 1, frame, "中:𠮷", 1, 3, 0);
        CollectionAssert.AreEquivalent(
            new[] { "中", ":", "𠮷" },
            data.Value.Load(id, 2).Glyphs.Keys.ToArray()
        );
    }

    /// <summary>绑定新修订后缺少标点参考必须NG，旧修订仍可完整比较。</summary>
    [TestMethod]
    public void MissingChineseOrPunctuationReferenceIsNgNotSilentlySkipped()
    {
        using var data = new Store();
        var frame = Blocks();
        string id = data.Value.CreateLibrary("font");
        data.Value.PutSheet(id, 1, frame, "中:𠮷", 1, 3, 0);
        using var backend = new OpenCvInspectionBackend(libraries: data.Value);
        using var engine = new InspectionEngine(backend);
        InspectionReport Inspect(int revision)
        {
            var roi = new InspectionRegion(
                "text",
                ERegionKind.Text,
                new PixelRect(0, 0, 72, 32),
                true,
                new FieldSettings(id, revision, expected: "中:𠮷", equalCells: true)
            );
            var recipe = new InspectionRecipe(
                "unicode",
                72,
                32,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { roi }
            );
            return engine.Inspect(TestRequests.FromSnapshot(frame, recipe));
        }
        var good = Inspect(2);
        Assert.AreEqual(
            EInspectionVerdict.Ok,
            good.Verdict,
            string.Join(";", good.Findings.Select(f => f.Code + ":" + f.Message))
        );
        Assert.AreEqual(3, good.Analysis.Regions[0].Glyphs.Count);
        data.Value.RemoveGlyph(id, 2, ":");
        var missing = Inspect(3);
        Assert.AreEqual(EInspectionVerdict.Ng, missing.Verdict);
        Assert.IsTrue(
            missing.Analysis.Regions[0].Findings.Any(f => f.Code == "missing_template"),
            string.Join(";", missing.Analysis.Regions[0].Findings.Select(f => f.Code + ":" + f.Message))
        );
        Assert.AreEqual(EInspectionVerdict.Ok, Inspect(2).Verdict);
    }

    /// <summary>真实字体合成图片中的中文与标点能制库并进行正式逐字比较。</summary>
    [TestMethod]
    public async Task RenderedChineseLineIsComparedIncludingPunctuation()
    {
        const string text = "中文标签：100，合格。";
        using var data = new Store();
        var frame = new OpenCvImageCodec().Decode(
            File.ReadAllBytes(
                Path.Combine(AppContext.BaseDirectory, "Text", "Fixtures", "glyph-unicode-line.png")
            )
        );
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        using var source = AlgorithmContractAdapter.ToVision(frame);
        using var backend = new OpenCvInspectionBackend(
            recognizer: new LineRecognizer(text),
            libraries: data.Value
        );
        using var engine = new InspectionEngine(backend);
        var extracted = await engine.ExtractGlyphCandidatesAsync(source, bounds, text);
        Assert.IsTrue(A.CharacterIdentity.TryTokenizeLine(text, out var identities));
        CollectionAssert.AreEqual(
            identities,
            extracted.Segmentation.Characters.Select(c => c.Character).ToArray(),
            extracted.Segmentation.Reason
        );
        string id = data.Value.CreateLibrary("中文标签");
        data.Value.PutGlyphs(
            id,
            1,
            extracted
                .Segmentation.Characters.GroupBy(c => c.Character)
                .Select(g => new GlyphImportItem(g.Key, g.First().Patch))
        );
        var roi = new InspectionRegion(
            "text",
            ERegionKind.Text,
            bounds,
            true,
            new FieldSettings(id, 2, expected: text)
        );
        var recipe = new InspectionRecipe(
            "unicode",
            frame.Width,
            frame.Height,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[] { roi }
        );
        var report = engine.Inspect(TestRequests.FromSnapshot(frame, recipe));
        Assert.AreEqual(text, report.Analysis.Regions[0].Recognition!.Text);
        Assert.AreEqual(
            EInspectionVerdict.Ok,
            report.Verdict,
            string.Join(";", report.Analysis.Regions[0].Findings.Select(f => f.Code + ":" + f.Message))
        );
        CollectionAssert.AreEqual(
            identities,
            report.Analysis.Regions[0].Glyphs.Select(g => g.Character.Character).ToArray()
        );
        Assert.IsTrue(report.Analysis.Regions[0].Glyphs.All(g => g.Status == "compared"));

        // 身份仍然正确，但把冒号上面的点擦掉，质量检查不能因OCR成功而放行。
        var colon = extracted.Segmentation.Characters.Single(c => c.Character == "：").Bounds;
        var pixels = frame.CopyPixels();
        int channels = frame.Format == EImagePixelFormat.Gray8 ? 1 : 3;
        for (int y = colon.Y; y < colon.Y + colon.Height / 2; y++)
        for (int x = colon.X; x < colon.X + colon.Width; x++)
        for (int channel = 0; channel < channels; channel++)
            pixels[(y * frame.Width + x) * channels + channel] = 255;
        var damaged = new PixelSnapshot(frame.Width, frame.Height, frame.Format, pixels);
        var rejected = engine.Inspect(TestRequests.FromSnapshot(damaged, recipe));
        Assert.AreEqual(text, rejected.Analysis.Regions[0].Recognition!.Text);
        Assert.AreEqual(EInspectionVerdict.Ng, rejected.Verdict);
        Assert.IsTrue(
            rejected
                .Analysis.Regions[0]
                .Glyphs.Any(g => g.Character.Character == "：" && g.Status != "compared")
        );
    }

    /// <summary>纯标点行也有逐字符像素证据，不再要求至少一个字母数字。</summary>
    [TestMethod]
    public async Task PunctuationOnlyLineProducesAllCandidates()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var source = AlgorithmContractAdapter.ToVision(Blocks());
        var result = await engine.ExtractGlyphCandidatesAsync(source, new PixelRect(0, 0, 72, 32), ":./");
        CollectionAssert.AreEqual(
            new[] { ":", ".", "/" },
            result.Segmentation.Characters.Select(c => c.Character).ToArray()
        );
    }

    /// <summary>扩大字符表后仍保留数量和总像素预算，失败不发布半份版本。</summary>
    [TestMethod]
    public void ResourceBudgetsRejectBeforePublishing()
    {
        using var data = new Store();
        string id = data.Value.CreateLibrary("budget");
        var image = new PixelSnapshot(512, 512, EImagePixelFormat.Gray8, new byte[512 * 512]);
        var entries = Enumerable
            .Range(0, 62)
            .Select(i => new GlyphImportItem(((char)(0x4e00 + i)).ToString(), image))
            .ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => data.Value.PutGlyphs(id, 1, entries));
        Assert.AreEqual(1, data.Value.ListLibraries()[0].Revision);
        var draft = new GlyphDraftSession();
        draft.LoadImage(image);
        var first = entries
            .Take(31)
            .Select(e => draft.AddManual(new PixelRect(0, 0, 512, 512), e.Character))
            .ToArray();
        draft.Stage(id, first, Array.Empty<string>());
        draft.LoadImage(image);
        var second = entries
            .Skip(31)
            .Select(e => draft.AddManual(new PixelRect(0, 0, 512, 512), e.Character))
            .ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => draft.Stage(id, second, Array.Empty<string>()));
        Assert.AreEqual(31, draft.Pending.Count);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new GlyphLibrarySnapshot(
                id,
                1,
                "budget",
                Enumerable
                    .Range(0, 4097)
                    .Select(i => new GlyphReference(char.ConvertFromUtf32(0x4e00 + i), Blocks(1), "hash"))
            )
        );
    }

    private sealed class LineRecognizer : A.ITextLineRecognizer
    {
        private readonly string _text;

        internal LineRecognizer(string text) => _text = text;

        public A.TextLineRecognition Recognize(
            DP.Vision.IImageSource frame,
            A.PixelBounds bounds,
            System.Threading.CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            A.CharacterIdentity.TryTokenizeLine(_text, out var labels);
            return new A.TextLineRecognition(
                bounds,
                "test",
                320,
                48,
                labels.Select((_, i) => new A.CtcStep(i + 1, .99f)),
                labels.Select((c, i) => new A.CtcToken(c, i, i + 1, .99f))
            );
        }

        public void Dispose() { }
    }

    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "glyph-unicode-" + Guid.NewGuid().ToString("N")
        );
        internal InspectionStore Value { get; }

        internal Store() => Value = new InspectionStore(_root, new OpenCvImageCodec());

        public void Dispose() => Directory.Delete(_root, true);
    }
}
