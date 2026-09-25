using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>字库条目二值化模式名与算法枚举的映射，以及中点模式端到端比较。</summary>
[TestClass]
public sealed class GlyphBinarizationTests
{
    private static ImageFrame Block(byte ink, byte paper)
    {
        var pixels = Enumerable.Repeat(paper, 32 * 32).ToArray();
        for (int y = 6; y < 26; y++)
        {
            for (int x = 10; x < 20; x++)
            {
                pixels[y * 32 + x] = ink;
            }
        }

        return new ImageFrame(32, 32, EImagePixelFormat.Gray8, pixels);
    }

    /// <summary>三种模式名均可保存；未知名称仍被拒绝。</summary>
    [TestMethod]
    public void ModeNamesMapToAlgorithm()
    {
        Assert.AreEqual(A.EGlyphBinarization.Otsu, Bridge.ToVisionBinarization("otsu"));
        Assert.AreEqual(A.EGlyphBinarization.Midpoint, Bridge.ToVisionBinarization("midpoint"));
        Assert.AreEqual(A.EGlyphBinarization.Fixed, Bridge.ToVisionBinarization("fixed"));
        Assert.AreEqual("midpoint", new GlyphReference("A", Block(0, 255), "h", "midpoint").Binarization);
        Assert.ThrowsExactly<ArgumentException>(() => new GlyphReference("A", Block(0, 255), "h", "adaptive"));
    }

    /// <summary>中点模式按各自墨色/纸色比较：低对比度灰纸上的同一字形无差异。</summary>
    [TestMethod]
    public void MidpointComparesLowContrastCapture()
    {
        var reference = new GlyphReference("A", Block(0, 255), "h", "midpoint");
        var result = new GlyphComparer().Compare(Block(90, 170), reference, 160, 0);
        Assert.AreEqual("compared", result.Status);
        Assert.AreEqual(0d, result.Difference);
    }
}
