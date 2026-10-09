using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>逐字符异常训练采用精确Unicode单字边界，保留ASCII行和非法身份的原子校验。</summary>
[TestClass]
public sealed class CharacterTrainingBoundaryTests
{
    private static (AnomalyTrainingSession Session, AnomalyTrainingSample Sample) Extract(
        params string[] labels
    )
    {
        var session = new AnomalyTrainingSession();
        var image = session.AddImage(
            new PixelSnapshot(192, 40, EImagePixelFormat.Gray8, new byte[192 * 40]),
            "label.png"
        );
        var model = session.AddModel("序列号", EAnomalyTrainingKind.Characters);
        var sample = session.AddSample(image, model, new PixelRect(0, 0, 192, 40));
        session.SetExtraction(
            sample,
            new CharacterSegmentation(
                "provisional",
                "test",
                "projection",
                labels.Length,
                labels.Select(
                    (label, i) =>
                        new CharacterPatch(
                            label,
                            i,
                            new PixelRect(i * 20, 4, 12, 24),
                            new PixelSnapshot(12, 24, EImagePixelFormat.Gray8, new byte[288])
                        )
                )
            )
        );
        return (session, sample);
    }

    /// <summary>中文、标点、补充平面字符一起进入覆盖统计，不崩溃、不静默训练半行。</summary>
    [TestMethod]
    public void UnicodeIdentitiesAreIncludedInCoverage()
    {
        var (session, sample) = Extract("A", "-", "中", "：", "𠮷");
        Assert.AreEqual(5, session.CharacterCoverage().Count);
        Assert.IsTrue(sample.Include.All(include => include));
        Assert.IsNull(sample.Problem);
        Assert.AreEqual(0, session.Problems().Count);
    }

    /// <summary>修改成非法或组合身份失败，不污染原标签和入训选择。</summary>
    [TestMethod]
    public void InvalidEditsFailWithoutChangingValidUnicodeLine()
    {
        var (session, sample) = Extract("A", "：");
        foreach (string label in new[] { "", " ", "\n", "a\u0301", "\ud840" })
            Assert.ThrowsExactly<ArgumentException>(() => session.SetLabel(sample, 1, label));
        Assert.AreEqual("：", sample.Labels[1]);
        session.SetLabel(sample, 1, "𠮷");
        Assert.IsNull(sample.Problem);
        Assert.AreEqual(2, session.CharacterCoverage().Count);
    }

    /// <summary>可靠性边界仍保留：不可靠切割默认不自动选入训练，可明确人工选择。</summary>
    [TestMethod]
    public void UncertainSegmentationDoesNotAutoSelectUnicode()
    {
        var (session, sample) = Extract("中", "：");
        session.SetExtraction(
            sample,
            new CharacterSegmentation(
                "uncertain",
                "cut uncertain",
                "projection",
                2,
                sample.Segmentation!.Characters
            )
        );
        Assert.IsTrue(sample.Include.All(include => !include));
        Assert.IsNotNull(sample.Problem);
        session.SetInclude(sample, 0, true);
        Assert.AreEqual(1, session.CharacterCoverage().Count);
    }

    /// <summary>已有ASCII行照常自动选入，合法分组和历史训练键保持兼容。</summary>
    [TestMethod]
    public void ExistingAsciiLinesRemainIncluded()
    {
        var (session, sample) = Extract("A", "1", "b");
        Assert.IsNull(sample.Problem);
        Assert.IsTrue(sample.Include.All(include => include));
        CollectionAssert.AreEquivalent(
            new[] { "序列号/A", "序列号/1", "序列号/b" },
            session.CharacterCoverage().Select(c => c.Key).ToArray()
        );
    }
}
