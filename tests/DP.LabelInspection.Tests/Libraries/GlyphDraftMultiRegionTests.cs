using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>同图多ROI按显式所属标识隔离，不能靠几何相交清空候选。</summary>
[TestClass]
public sealed class GlyphDraftMultiRegionTests
{
    private static PixelSnapshot Image(int width = 256, int height = 128) =>
        new PixelSnapshot(
            width,
            height,
            EImagePixelFormat.Gray8,
            Enumerable.Repeat((byte)180, width * height).ToArray()
        );

    private static GlyphDraftSession Session()
    {
        var session = new GlyphDraftSession();
        session.LoadImage(Image(), "multi.png");
        return session;
    }

    private static GlyphCandidateExtraction Extract(GlyphDraftRegion region, string label, byte pixel = 20)
    {
        var bounds = new PixelRect(region.Bounds.X + 4, region.Bounds.Y + 4, 16, 20);
        var image = new PixelSnapshot(
            16,
            20,
            EImagePixelFormat.Gray8,
            Enumerable.Repeat(pixel, 320).ToArray()
        );
        return new GlyphCandidateExtraction(
            null,
            label,
            new CharacterSegmentation(
                "provisional",
                "test",
                "projection",
                1,
                new[] { new CharacterPatch(label, 0, bounds, image) }
            )
        );
    }

    /// <summary>新ROI提取追加候选，重提仅替换同ROI，另一区域的人工编辑和勾选所需稳定标识不变。</summary>
    [TestMethod]
    public void SameImageRegionsAccumulateAndReplaceIndependently()
    {
        var s = Session();
        var first = s.AddRegion(new PixelRect(0, 0, 128, 32));
        s.ApplyRegionExtraction(first.Id, Extract(first, "A"));
        var second = s.AddRegion(new PixelRect(0, 40, 128, 32));
        Assert.AreEqual("A", s.Candidates.Single().Label);
        s.ApplyRegionExtraction(second.Id, Extract(second, "B"));
        string id = s.Candidates.Single(c => c.RegionId == second.Id).Id;
        s.SetLabel(id, "中");
        s.ApplyRegionExtraction(first.Id, Extract(first, "：", 90));
        Assert.AreEqual(2, s.Candidates.Count);
        Assert.AreEqual("中", s.Candidates.Single(c => c.Id == id).Label);
        Assert.AreEqual("：", s.Candidates.Single(c => c.RegionId == first.Id).Label);
        Assert.AreEqual(90, s.Candidates.Single(c => c.RegionId == first.Id).Image.CopyPixels()[0]);
    }

    /// <summary>零候选和异常状态不能删掉旧像素；同一范围复用ROI，不重复注册。</summary>
    [TestMethod]
    public void FailedExtractionRetainsOldCandidatesAndEvidence()
    {
        var s = Session();
        var region = s.AddRegion(new PixelRect(0, 0, 128, 32));
        Assert.AreSame(region, s.AddRegion(region.Bounds));
        s.ApplyRegionExtraction(region.Id, Extract(region, "中"));
        var candidate = s.Candidates.Single();
        s.ApplyRegionExtraction(
            region.Id,
            new GlyphCandidateExtraction(
                null,
                "中",
                new CharacterSegmentation("uncertain", "no pixels", "none", 0, Array.Empty<CharacterPatch>())
            )
        );
        Assert.AreSame(candidate, s.Candidates.Single());
        Assert.AreEqual("no pixels", s.Regions.Single().Error);
        s.RecordRegionFailure(region.Id, "cancelled");
        Assert.AreSame(candidate, s.Candidates.Single());
        Assert.AreEqual("cancelled", s.Regions.Single().Error);
        s.ApplyRegionExtraction(region.Id, Extract(region, "文"));
        Assert.IsNull(s.Regions.Single().Error);
    }

    /// <summary>交叠ROI仍按归属隔离；删除一个区域不能删除另一区域或独立手工裁图。</summary>
    [TestMethod]
    public void RemovingOverlappingRegionDoesNotTouchOthersOrPending()
    {
        var s = Session();
        var first = s.AddRegion(new PixelRect(0, 0, 128, 32));
        var second = s.AddRegion(new PixelRect(0, 4, 128, 32));
        s.ApplyRegionExtraction(first.Id, Extract(first, "中"));
        s.ApplyRegionExtraction(second.Id, Extract(second, "文"));
        string manual = s.AddManual(new PixelRect(4, 4, 16, 20), ":");
        s.Stage("font", s.Candidates.Select(c => c.Id), Array.Empty<string>());
        s.RemoveRegion(first.Id);
        CollectionAssert.AreEquivalent(new[] { "文", ":" }, s.Candidates.Select(c => c.Label).ToArray());
        Assert.IsNotNull(s.Candidates.SingleOrDefault(c => c.Id == manual));
        Assert.AreEqual(3, s.Pending.Count);
        s.Undo();
        Assert.AreEqual(2, s.Regions.Count);
        Assert.AreEqual(3, s.Candidates.Count);
        s.Redo();
        Assert.AreEqual(1, s.Regions.Count);
        s.ClearRegions();
        Assert.AreEqual(manual, s.Candidates.Single().Id);
        Assert.AreEqual(3, s.Pending.Count);
    }

    /// <summary>ROI几何和其候选共同撤销，不能恢复到错误行的身份；改变范围不改清单。</summary>
    [TestMethod]
    public void RoiBoundsUndoRestoresItsCandidatesAtomically()
    {
        var s = Session();
        var region = s.AddRegion(new PixelRect(0, 0, 128, 32));
        s.ApplyRegionExtraction(region.Id, Extract(region, "中"));
        string id = s.Candidates.Single().Id;
        s.Stage("font", new[] { id }, Array.Empty<string>());
        s.SetRegionBounds(region.Id, new PixelRect(0, 40, 128, 32));
        Assert.AreEqual(0, s.Candidates.Count);
        Assert.AreEqual(1, s.Pending.Count);
        s.Undo();
        Assert.AreEqual(region.Bounds, s.Regions.Single().Bounds);
        Assert.AreEqual(id, s.Candidates.Single().Id);
        s.Redo();
        Assert.AreEqual(40, s.Regions.Single().Bounds.Y);
        Assert.AreEqual(0, s.Candidates.Count);
    }

    /// <summary>人工文字每ROI独立，和手动切分/标签修正一样不会串行或丢失候选所属。</summary>
    [TestMethod]
    public void TextEditingAndSplitKeepOwnershipAndProvenance()
    {
        var s = Session();
        var first = s.AddRegion(new PixelRect(0, 0, 128, 32));
        var second = s.AddRegion(new PixelRect(0, 40, 128, 32));
        s.SetRegionText(first.Id, "中文");
        s.SetRegionText(second.Id, "：");
        Assert.AreEqual("中文", s.Regions[0].DisplayText);
        Assert.AreEqual("：", s.Regions[1].DisplayText);
        s.ApplyRegionExtraction(first.Id, Extract(first, "中"));
        string id = s.Candidates.Single().Id;
        s.Split(id, 12);
        Assert.IsTrue(s.Candidates.All(c => c.RegionId == first.Id));
        s.SetLabel(s.Candidates[0].Id, "中");
        s.SetLabel(s.Candidates[1].Id, "文");
        s.Resize(s.Candidates[0].Id, new PixelRect(4, 4, 8, 20));
        s.Stage("font", s.Candidates.Select(c => c.Id), Array.Empty<string>());
        StringAssert.Contains(s.Pending[0].ProvenanceJson!, "\"source_roi_id\":\"" + first.Id + "\"");
        StringAssert.Contains(s.Pending[0].ProvenanceJson!, "ROI 1");
        s.LoadImage(Image(), "next.png");
        Assert.AreEqual(0, s.Regions.Count);
        Assert.AreEqual(0, s.Candidates.Count);
        Assert.AreEqual(2, s.Pending.Count);
        Assert.AreNotEqual(first.Id, s.AddRegion(first.Bounds).Id);
    }

    /// <summary>多个ROI累计可超过旧单行128候选上限；当前图仍受总像素预算约束。</summary>
    [TestMethod]
    public void CandidateBudgetAppliesToWholeImageNotOneLine()
    {
        var s = Session();
        for (int y = 0; y < 2; y++)
        {
            var region = s.AddRegion(new PixelRect(0, y * 40, 128, 32));
            var patch = Extract(region, "中").Segmentation.Characters[0];
            s.ApplyRegionExtraction(
                region.Id,
                new GlyphCandidateExtraction(
                    null,
                    null,
                    new CharacterSegmentation(
                        "provisional",
                        "test",
                        "test",
                        128,
                        Enumerable.Repeat(patch, 128)
                    )
                )
            );
        }
        Assert.AreEqual(256, s.Candidates.Count);
        var large = Image(512, 512);
        s.LoadImage(large);
        for (int i = 0; i < 61; i++)
            s.AddManual(new PixelRect(0, 0, 512, 512), "中");
        Assert.ThrowsExactly<ArgumentException>(() => s.AddManual(new PixelRect(0, 0, 512, 512), "文"));
        Assert.AreEqual(61, s.Candidates.Count);
    }

    /// <summary>ROI和单行数量预算、候选原图归属检查失败均不会部分覆盖已有候选。</summary>
    [TestMethod]
    public void RegionBudgetsAndInvalidProviderResultsAreAtomic()
    {
        var s = Session();
        for (int i = 0; i < GlyphDraftSession.MaximumRegions; i++)
            s.AddRegion(new PixelRect(i * 4, 0, 64, 32));
        Assert.ThrowsExactly<ArgumentException>(() => s.AddRegion(new PixelRect(0, 40, 64, 32)));
        Assert.AreEqual(32, s.Regions.Count);
        s.ApplyRegionExtraction(s.Regions[0].Id, Extract(s.Regions[0], "A"));
        var existing = s.Candidates.Single();
        var outside = Extract(s.Regions[0], "B");
        Assert.ThrowsExactly<ArgumentException>(() => s.ApplyRegionExtraction(s.Regions[31].Id, outside));
        Assert.AreSame(existing, s.Candidates.Single());
        var patch = outside.Segmentation.Characters[0];
        var oversized = new GlyphCandidateExtraction(
            null,
            "B",
            new CharacterSegmentation("provisional", "test", "test", 129, Enumerable.Repeat(patch, 129))
        );
        Assert.ThrowsExactly<ArgumentException>(() => s.ApplyRegionExtraction(s.Regions[0].Id, oversized));
        Assert.AreSame(existing, s.Candidates.Single());
    }
}
