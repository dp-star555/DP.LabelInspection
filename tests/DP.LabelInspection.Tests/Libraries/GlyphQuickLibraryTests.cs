using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>参考制作与检测判定独立，采用串行提取及原子发布。</summary>
[TestClass]
public sealed partial class GlyphQuickLibraryTests
{
    private static ImageFrame Line()
    {
        var pixels = Enumerable.Repeat((byte)255, 64 * 32).ToArray();
        for (int y = 6; y < 26; y++)
        {
            for (int x = 6; x < 16; x++)
            {
                pixels[y * 64 + x] = 0;
                pixels[y * 64 + x + 28] = 0;
            }
        }

        return new ImageFrame(64, 32, EImagePixelFormat.Gray8, pixels);
    }

    private static GlyphImportItem Item(string character, string? provenance = null)
    {
        return new GlyphImportItem(
            character,
            Line().Crop(new PixelRect(4, 4, 16, 24)),
            provenanceJson: provenance
        );
    }

    /// <summary>多个独立参考发布为一个版本，保留原空版本历史。</summary>
    [TestMethod]
    public void BatchPublishesOneRevision()
    {
        using var store = new Store();
        var id = store.Value.CreateLibrary("regular");
        Assert.AreEqual(2, store.Value.PutGlyphs(id, 1, new[] { Item("A"), Item("a") }));
        Assert.AreEqual(2, store.Value.Load(id, 2).Glyphs.Count);
        Assert.AreEqual(0, store.Value.Load(id, 1).Glyphs.Count);
    }

    /// <summary>重复标签不能静默选择最后一个样本。</summary>
    [TestMethod]
    public void DuplicateBatchIsRejected()
    {
        using var store = new Store();
        var id = store.Value.CreateLibrary("regular");
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.Value.PutGlyphs(id, 1, new[] { Item("A"), Item("A") })
        );
        Assert.AreEqual(1, store.Value.ListLibraries()[0].Revision);
    }

    /// <summary>任何无效来源记录都在发布前中止全部条目。</summary>
    [TestMethod]
    public void InvalidSecondItemDoesNotPartiallyPublish()
    {
        using var store = new Store();
        var id = store.Value.CreateLibrary("regular");
        Assert.ThrowsExactly<Newtonsoft.Json.JsonReaderException>(() =>
            store.Value.PutGlyphs(id, 1, new[] { Item("A"), Item("B", "not-json") })
        );
        Assert.AreEqual(1, store.Value.ListLibraries()[0].Revision);
        Assert.AreEqual(0, store.Value.Load(id, 1).Glyphs.Count);
    }

    /// <summary>替换已有标签需要明确允许，仍拒绝过期版本。</summary>
    [TestMethod]
    public void ReplacementAndStaleEditsAreExplicit()
    {
        using var store = new Store();
        var id = store.Value.CreateLibrary("regular");
        store.Value.PutGlyphs(id, 1, new[] { Item("A") });
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.Value.PutGlyphs(id, 2, new[] { Item("B"), Item("A") })
        );
        Assert.AreEqual(2, store.Value.ListLibraries()[0].Revision);
        Assert.AreEqual(3, store.Value.PutGlyphs(id, 2, new[] { Item("A") }, true));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.Value.PutGlyphs(id, 2, new[] { Item("B") })
        );
        Assert.AreEqual(1, store.Value.Load(id, 3).Glyphs.Count);
    }

    /// <summary>字符参考有意限制为ASCII字母数字及有界图像。</summary>
    [TestMethod]
    public void ReferenceLimitsAreNotHidden()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Item("字"));
        Assert.ThrowsExactly<ArgumentException>(() => Item("AB"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new GlyphImportItem("A", new ImageFrame(3, 4, EImagePixelFormat.Gray8, new byte[12]))
        );
    }

    /// <summary>人工标注单行使用真实分割，不需要识别器或已有字库。</summary>
    [TestMethod]
    public async Task ManualLineNeedsNoOcrOrLibrary()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(Line(), new PixelRect(0, 0, 64, 32), "A0");
        Assert.IsNull(result.Recognition);
        Assert.AreEqual("A0", result.ConfirmedText);
        Assert.AreEqual(2, result.Segmentation.Characters.Count);
        Assert.AreEqual("0", result.Segmentation.Characters[1].Character);
    }

    /// <summary>不支持的人工文本会被拒绝，不静默归一化。</summary>
    [TestMethod]
    public async Task ManualLabelsAreNotCorrected()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            engine.ExtractGlyphCandidatesAsync(Line(), new PixelRect(0, 0, 64, 32), "A\nO")
        );
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            engine.ExtractGlyphCandidatesAsync(Line(), new PixelRect(0, 0, 64, 32))
        );
    }

    /// <summary>已释放或调用前已取消的引擎不执行候选提取。</summary>
    [TestMethod]
    public async Task CandidateLifetimeIsProtected()
    {
        using var backend = new OpenCvInspectionBackend();
        var engine = new InspectionEngine(backend);
        engine.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            engine.ExtractGlyphCandidatesAsync(Line(), new PixelRect(0, 0, 64, 32), "AB")
        );
        using var live = new InspectionEngine(backend);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await live.ExtractGlyphCandidatesAsync(Line(), new PixelRect(0, 0, 64, 32), "AB", cancel.Token);
            Assert.Fail("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>候选任务与实际检测共用执行关卡，不另设不安全的识别器锁。</summary>
    [TestMethod]
    public async Task InspectionAndCandidatesAreSerialized()
    {
        using var backend = new GateBackend();
        using var engine = new InspectionEngine(backend);
        var frame = Line();
        var request = new InspectionRequest(
            frame,
            new InspectionRecipe(
                "test",
                64,
                32,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                Array.Empty<InspectionRegion>()
            )
        );
        await Task.WhenAll(
            Enumerable
                .Range(0, 12)
                .Select(i =>
                    i % 2 == 0
                        ? (Task)engine.InspectAsync(request)
                        : engine.ExtractGlyphCandidatesAsync(frame, new PixelRect(0, 0, 64, 32), "AB")
                )
        );
        Assert.IsFalse(backend.Overlap);
    }
}
