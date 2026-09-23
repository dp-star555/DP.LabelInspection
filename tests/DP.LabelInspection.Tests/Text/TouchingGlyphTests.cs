using System;
using System.Linq;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>参考制作可提供有界待复核候选，生产分割仍保持保守。</summary>
[TestClass]
public sealed class TouchingGlyphTests
{
    private static ImageFrame Line(int bridgeHeight)
    {
        var p = Enumerable.Repeat((byte)255, 130 * 36).ToArray();
        for (int i = 0; i < 8; i++)
        {
            for (int y = 7; y < 29; y++)
            {
                for (int x = 6 + i * 15; x < 16 + i * 15; x++)
                {
                    p[y * 130 + x] = 0;
                }
            }
        }

        for (int y = 15; y < 15 + bridgeHeight; y++)
        {
            for (int x = 16; x < 21; x++)
            {
                p[y * 130 + x] = 0;
            }
        }

        return new ImageFrame(130, 36, EImagePixelFormat.Gray8, p);
    }

    /// <summary>一个细连接字符对不能导致其余已分离的八字符参考行全部不可用。</summary>
    [TestMethod]
    public async Task ThinJoinOffersReviewedCandidatesOnly()
    {
        var frame = Line(1);
        var roi = new PixelRect(0, 0, 130, 36);
        var original = new CharacterSegmenter().Segment(frame, roi, "WF675907");
        Assert.AreEqual(0, original.Characters.Count);
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(frame, roi, "WF675907");
        Assert.AreEqual(8, result.Segmentation.Characters.Count);
        Assert.AreEqual("review_required", result.Segmentation.Status);
        Assert.AreEqual(7, result.Segmentation.PhysicalCount);
        Assert.AreEqual(0, new CharacterSegmenter().Segment(frame, roi, "WF675907").Characters.Count);
    }

    /// <summary>不能仅为符合提供的字符数而切开宽连接。</summary>
    [TestMethod]
    public async Task BroadJoinIsNotForced()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(
            Line(12),
            new PixelRect(0, 0, 130, 36),
            "WF675907"
        );
        Assert.AreEqual(0, result.Segmentation.Characters.Count);
    }

    /// <summary>清晰分离的字形保留原有待确认路径。</summary>
    [TestMethod]
    public async Task ClearLineIsUnchanged()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(
            Line(0),
            new PixelRect(0, 0, 130, 36),
            "WF675907"
        );
        Assert.AreEqual(8, result.Segmentation.Characters.Count);
        Assert.AreEqual("provisional", result.Segmentation.Status);
    }

    /// <summary>错误的提供数量不构成切开普通字形的证据。</summary>
    [TestMethod]
    public async Task WrongCountIsNotForced()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(
            Line(0),
            new PixelRect(0, 0, 130, 36),
            "WF6759079"
        );
        Assert.AreEqual(0, result.Segmentation.Characters.Count);
    }

    /// <summary>源笔画被裁断时，不能转换为看似可用的参考。</summary>
    [TestMethod]
    public async Task ClippedLineStillRejects()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(
            Line(1),
            new PixelRect(0, 8, 130, 28),
            "WF675907"
        );
        Assert.AreEqual(0, result.Segmentation.Characters.Count);
        StringAssert.Contains(result.Segmentation.Reason, "ROI");
    }

    /// <summary>切分两侧保留全部阈值化源墨迹，候选边界不能擦除连接部分。</summary>
    [TestMethod]
    public async Task CandidateSplitDoesNotEraseInk()
    {
        var frame = Line(1);
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = await engine.ExtractGlyphCandidatesAsync(
            frame,
            new PixelRect(0, 0, 130, 36),
            "WF675907"
        );
        Assert.AreEqual(8, result.Segmentation.Characters.Count);
        int original = frame.CopyPixels().Count(v => v == 0),
            owned = result.Segmentation.Characters.Sum(c => c.Patch.CopyPixels().Count(v => v == 0));
        Assert.AreEqual(original, owned);
        Assert.AreEqual(0, result.Segmentation.Characters.Sum(c => c.NeighborInkRemoved));
    }
}
