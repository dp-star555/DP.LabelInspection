using System;
using DP.LabelInspection.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace DP.LabelInspection.Tests;

/// <summary>厂商中立的几何所有权及坐标契约测试。</summary>
[TestClass]
public sealed class CanvasGeometryTests
{
    /// <summary>游程保留孔洞、排他右端和图像外坐标，不合并对象。</summary>
    [TestMethod]
    public void RegionRunsRetainPixelMembership()
    {
        var runs = new[] { new CanvasRun(-1, -2, 2), new CanvasRun(0, 0, 2), new CanvasRun(0, 4, 6) };
        var region = new CanvasRegion("r", runs);
        runs[1] = new CanvasRun(0, 10, 20);
        Assert.AreEqual(8L, region.AreaPixels);
        Assert.IsTrue(region.Contains(1, 0));
        Assert.IsFalse(region.Contains(2, 0));
        Assert.IsFalse(region.Contains(3, 0));
        Assert.IsTrue(region.Contains(4, 0));
        Assert.IsTrue(region.Contains(-2, -1));
        var scene = new CanvasGeometry(
            new[] { region, new CanvasRegion("empty", Array.Empty<CanvasRun>()) },
            Array.Empty<CanvasPolyline>()
        );
        var copy = JsonConvert.DeserializeObject<CanvasGeometry>(JsonConvert.SerializeObject(scene))!;
        Assert.AreEqual(2, copy.Regions.Count);
        Assert.AreEqual(0, copy.Regions[1].Runs.Count);
        Assert.AreEqual(8L, copy.Regions[0].AreaPixels);
    }

    /// <summary>亚像素位置和显式闭合状态不受调用方修改影响，并可通过JSON往返保留。</summary>
    [TestMethod]
    public void ContourIsOwnedAndSubpixel()
    {
        var points = new[] { new CanvasPoint(12.125, 7.75), new CanvasPoint(20.5, 12.25) };
        var contour = new CanvasPolyline("c", points, false);
        points[0] = new CanvasPoint(0, 0);
        var scene = new CanvasGeometry(Array.Empty<CanvasRegion>(), new[] { contour });
        var copy = JsonConvert.DeserializeObject<CanvasGeometry>(JsonConvert.SerializeObject(scene))!;
        Assert.AreEqual(12.125, copy.Contours[0].Points[0].X);
        Assert.AreEqual(7.75, copy.Contours[0].Points[0].Y);
        Assert.IsFalse(copy.Contours[0].Closed);
        foreach (var reference in typeof(CanvasGeometry).Assembly.GetReferencedAssemblies())
        {
            Assert.IsFalse(reference.Name!.StartsWith("halcon", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>在原生绘制前拒绝畸形几何。</summary>
    [TestMethod]
    public void InvalidRunsAndCoordinatesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CanvasPoint(double.NaN, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new CanvasRun(1, 4, 4));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CanvasRegion("r", new[] { new CanvasRun(0, 0, 4), new CanvasRun(0, 3, 5) })
        );
        Assert.ThrowsExactly<ArgumentException>(() => new CanvasRegion("r", new[] { default(CanvasRun) }));
    }
}
