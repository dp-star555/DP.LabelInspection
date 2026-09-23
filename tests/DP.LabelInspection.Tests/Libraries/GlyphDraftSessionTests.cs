using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>增量参考制作和手动分割共用与UI无关的状态机。</summary>
[TestClass]
public sealed partial class GlyphDraftSessionTests
{
    private static ImageFrame Image(byte value = 20)
    {
        return new ImageFrame(32, 16, EImagePixelFormat.Gray8, Enumerable.Repeat(value, 512).ToArray());
    }

    private static string Add(GlyphDraftSession session, string label = "A")
    {
        return session.AddManual(new PixelRect(2, 2, 20, 12), label);
    }

    /// <summary>多图导入后，暂存像素保留各自来源标识。</summary>
    [TestMethod]
    public void StagingSurvivesMultipleImages()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image(20), "first.png");
        s.Stage("font", new[] { Add(s, "A") }, Array.Empty<string>());
        s.LoadImage(Image(90), "second.png");
        Assert.AreEqual(0, s.Candidates.Count);
        Assert.AreEqual(1, s.Pending.Count);
        s.Stage("font", new[] { Add(s, "B") }, Array.Empty<string>());
        Assert.AreEqual(20, s.Pending[0].Image.CopyPixels()[0]);
        Assert.AreEqual(90, s.Pending[1].Image.CopyPixels()[0]);
        StringAssert.Contains(s.Pending[0].ProvenanceJson!, "first.png");
        StringAssert.Contains(s.Pending[1].ProvenanceJson!, "second.png");
    }

    /// <summary>重复会话追加缺失字符，不丢失旧版本或已有字符。</summary>
    [TestMethod]
    public void PublishThenContinueFromAnotherImage()
    {
        using var store = new Store();
        string id = store.Value.CreateLibrary("font");
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        s.Stage(id, new[] { Add(s, "A") }, Array.Empty<string>());
        Assert.AreEqual(2, s.Publish(store.Value, id, 1));
        Assert.AreEqual(0, s.Pending.Count);
        s.LoadImage(Image(90));
        s.Stage(id, new[] { Add(s, "B") }, new[] { "A" });
        Assert.AreEqual(3, s.Publish(store.Value, id, 2));
        Assert.AreEqual(2, store.Value.Load(id, 3).Glyphs.Count);
        Assert.AreEqual(1, store.Value.Load(id, 2).Glyphs.Count);
    }

    /// <summary>已有和重复标签会报告，不阻止添加另一缺失字符。</summary>
    [TestMethod]
    public void ExistingCharacterDoesNotBlockNewOne()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        var skipped = s.Stage("font", new[] { Add(s, "A"), Add(s, "B"), Add(s, "B") }, new[] { "A" });
        CollectionAssert.AreEqual(new[] { "A", "B" }, skipped.ToArray());
        Assert.AreEqual("B", s.Pending.Single().Character);
    }

    /// <summary>自动结果为空不阻止明确的人工裁剪、切分和标注。</summary>
    [TestMethod]
    public void ManualSegmentationWorksWithoutAutomaticCandidates()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        var original = new CharacterSegmentation(
            "uncertain",
            "none",
            "touching",
            1,
            Array.Empty<CharacterPatch>()
        );
        s.ApplyExtraction(original);
        string id = Add(s, "");
        s.Split(id, 12);
        Assert.IsTrue(s.Candidates.All(c => c.Label == ""));
        s.SetLabel(s.Candidates[0].Id, "W");
        s.SetLabel(s.Candidates[1].Id, "F");
        s.Stage("font", s.Candidates.Select(c => c.Id), Array.Empty<string>());
        Assert.AreEqual(2, s.Pending.Count);
        Assert.AreEqual(0, original.Characters.Count);
        StringAssert.Contains(s.Pending[0].ProvenanceJson!, "manual_split");
    }

    /// <summary>手动切分完整划分每个BGR源像素，不擦除、插值或重叠。</summary>
    [TestMethod]
    public void SplitPreservesAllPixels()
    {
        var image = new ImageFrame(
            32,
            16,
            EImagePixelFormat.Bgr24,
            Enumerable.Range(0, 32 * 16 * 3).Select(i => (byte)(i % 251)).ToArray()
        );
        var s = new GlyphDraftSession();
        s.LoadImage(image);
        var id = Add(s, "W");
        var before = s.Candidates[0].Image.CopyPixels();
        s.Split(id, 13);
        var a = s.Candidates[0].Image.CopyPixels();
        var b = s.Candidates[1].Image.CopyPixels();
        var assembled = Enumerable
            .Range(0, 12)
            .SelectMany(y => a.Skip(y * 11 * 3).Take(11 * 3).Concat(b.Skip(y * 9 * 3).Take(9 * 3)))
            .ToArray();
        CollectionAssert.AreEqual(before, assembled);
        Assert.AreEqual(13, s.Candidates[1].Bounds.X);
        Assert.IsTrue(s.Candidates.All(c => c.Label.Length == 0));
    }

    /// <summary>拒绝切分不会破坏撤销/重做状态，也不虚构过小参考图块。</summary>
    [TestMethod]
    public void InvalidCutPreservesHistory()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        string id = Add(s);
        s.Split(id, 12);
        s.Undo();
        Assert.IsTrue(s.CanRedo);
        Assert.ThrowsExactly<ArgumentException>(() => s.Split(id, 3));
        Assert.AreEqual(1, s.Candidates.Count);
        Assert.IsTrue(s.CanRedo);
        s.Redo();
        Assert.AreEqual(2, s.Candidates.Count);
    }

    /// <summary>无标签选择不能静默发布，暂存失败保留原列表。</summary>
    [TestMethod]
    public void InvalidStagingIsAtomic()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        Assert.ThrowsExactly<ArgumentException>(() =>
            s.Stage("font", new[] { Add(s, "A"), Add(s, "") }, Array.Empty<string>())
        );
        Assert.AreEqual(0, s.Pending.Count);
        Assert.IsNull(s.PendingLibraryId);
    }

    /// <summary>已复核暂存拥有不可变像素，不受后续裁剪编辑影响。</summary>
    [TestMethod]
    public void ResizeAndUndoDoNotMutateStagedPatch()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        string id = Add(s);
        s.Stage("font", new[] { id }, Array.Empty<string>());
        s.Resize(id, new PixelRect(4, 4, 8, 8));
        Assert.AreEqual(8, s.Candidates[0].Image.Width);
        Assert.AreEqual(20, s.Pending[0].Image.Width);
        s.Undo();
        Assert.AreEqual(20, s.Candidates[0].Image.Width);
        s.Redo();
        Assert.AreEqual(8, s.Candidates[0].Image.Width);
    }

    /// <summary>过期版本发布失败保留队列，等待明确刷新和重试。</summary>
    [TestMethod]
    public void StaleSaveRetainsAllPendingItems()
    {
        using var store = new Store();
        string id = store.Value.CreateLibrary("font");
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        s.Stage(id, new[] { Add(s, "A") }, Array.Empty<string>());
        store.Value.PutGlyphs(id, 1, new[] { new GlyphImportItem("B", Image()) });
        Assert.ThrowsExactly<InvalidOperationException>(() => s.Publish(store.Value, id, 1));
        Assert.AreEqual(1, s.Pending.Count);
        Assert.AreEqual(3, s.Publish(store.Value, id, 2));
        Assert.AreEqual(2, store.Value.Load(id, 3).Glyphs.Count);
    }

    /// <summary>跨图暂存不能静默切换字体或类别。</summary>
    [TestMethod]
    public void PendingListIsBoundToOneLibrary()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        string id = Add(s);
        s.Stage("font-a", new[] { id }, Array.Empty<string>());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            s.Stage("font-b", new[] { id }, Array.Empty<string>())
        );
        s.ClearPending();
        s.Stage("font-b", new[] { id }, Array.Empty<string>());
        Assert.AreEqual("font-b", s.PendingLibraryId);
    }

    /// <summary>来源记录中的待确认身份不被人工标签覆盖。</summary>
    [TestMethod]
    public void LabelCorrectionRetainsProvisionalIdentity()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        s.ApplyExtraction(
            new CharacterSegmentation(
                "provisional",
                "projection",
                "test",
                1,
                new[]
                {
                    new CharacterPatch(
                        "O",
                        0,
                        new PixelRect(2, 2, 8, 8),
                        Image().Crop(new PixelRect(2, 2, 8, 8))
                    ),
                }
            )
        );
        var id = s.Candidates[0].Id;
        s.SetLabel(id, "0");
        s.Stage("font", new[] { id }, Array.Empty<string>());
        Assert.AreEqual("0", s.Pending[0].Character);
        StringAssert.Contains(s.Pending[0].ProvenanceJson!, "\"provisional_character\":\"O\"");
    }

    /// <summary>已保存但不可用的样本会跳过，不阻止有效新字符。</summary>
    [TestMethod]
    public void SkippedExistingSmallPatchDoesNotBlockNewOne()
    {
        var s = new GlyphDraftSession();
        s.LoadImage(Image());
        s.ApplyExtraction(
            new CharacterSegmentation(
                "provisional",
                "projection",
                "test",
                1,
                new[]
                {
                    new CharacterPatch(
                        "A",
                        0,
                        new PixelRect(2, 2, 3, 8),
                        Image().Crop(new PixelRect(2, 2, 3, 8))
                    ),
                }
            )
        );
        Add(s, "B");
        var skipped = s.Stage("font", s.Candidates.Select(c => c.Id), new[] { "A" });
        Assert.AreEqual("A", skipped.Single());
        Assert.AreEqual("B", s.Pending.Single().Character);
    }

    /// <summary>显式替换仍需发布确认，不会静默覆盖。</summary>
    [TestMethod]
    public void ReplacementRequiresExplicitPublishConsent()
    {
        using var store = new Store();
        string id = store.Value.CreateLibrary("font");
        store.Value.PutGlyphs(id, 1, new[] { new GlyphImportItem("A", Image(10)) });
        var s = new GlyphDraftSession();
        s.LoadImage(Image(90), "quoted\"source.png");
        s.Stage(id, new[] { Add(s) }, new[] { "A" }, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => s.Publish(store.Value, id, 2));
        Assert.AreEqual(1, s.Pending.Count);
        Assert.AreEqual(3, s.Publish(store.Value, id, 2, true));
        Assert.AreEqual(90, store.Value.Load(id, 3).Glyphs["A"].Image.CopyPixels()[0]);
    }
}
