using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>统一制库中的二值化模式随暂存快照冻结，默认行为保持兼容。</summary>
[TestClass]
public sealed class GlyphDraftBinarizationTests
{
    /// <summary>支持的参考模式原样进入待入库清单。</summary>
    /// <param name="mode">本次选择的二值化模式。</param>
    [TestMethod]
    [DataRow("otsu")]
    [DataRow("fixed")]
    [DataRow("midpoint")]
    public void Stage_FreezesSelectedReferenceMode(string mode)
    {
        var session = Session();
        string id = session.AddManual(new PixelRect(0, 0, 24, 40), "中");
        session.Stage("library", new[] { id }, Array.Empty<string>(), binarization: mode);
        Assert.AreEqual(mode, session.Pending.Single().Binarization);
        session.SetLabel(id, "文");
        Assert.AreEqual("中", session.Pending.Single().Character);
        Assert.AreEqual(mode, session.Pending.Single().Binarization);
    }

    /// <summary>非法模式不会部分写入清单。</summary>
    [TestMethod]
    public void Stage_InvalidReferenceMode_PreservesPendingSnapshot()
    {
        var session = Session();
        string first = session.AddManual(new PixelRect(0, 0, 24, 40), "A");
        string second = session.AddManual(new PixelRect(0, 0, 24, 40), "B");
        session.Stage("library", new[] { first }, Array.Empty<string>(), binarization: "midpoint");
        Assert.ThrowsExactly<ArgumentException>(() =>
            session.Stage("library", new[] { second }, Array.Empty<string>(), binarization: "unknown"));
        Assert.AreEqual(1, session.Pending.Count);
        Assert.AreEqual("A", session.Pending.Single().Character);
        Assert.AreEqual("midpoint", session.Pending.Single().Binarization);
    }

    private static GlyphDraftSession Session()
    {
        var session = new GlyphDraftSession();
        session.LoadImage(new PixelSnapshot(24, 40, EImagePixelFormat.Gray8,
            Enumerable.Repeat((byte)180, 24 * 40).ToArray()));
        return session;
    }
}
