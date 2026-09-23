using System;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>单向业务到通用模块适配，不修改原始证据。</summary>
[TestClass]
public sealed class VisionAdapterTests
{
    /// <summary>转换保留Region对象、排他右端游程及空轮廓。</summary>
    [TestMethod]
    public void ObjectsAndEmptyGeometryPreserved()
    {
        var source = new CanvasGeometry(
            new[]
            {
                new CanvasRegion("r", new[] { new CanvasRun(2, 3, 5) }),
                new CanvasRegion("empty", Array.Empty<CanvasRun>()),
            },
            new[] { new CanvasPolyline("empty-xld", Array.Empty<CanvasPoint>(), false) }
        );
        var target = VisionAdapter.ConvertGeometry("f", source);
        Assert.AreEqual(2, target.Layers[0].Visuals.Count);
        var region = (DP.Vision.RegionGeometry)target.Layers[0].Visuals[0].Geometry;
        Assert.AreEqual(2L, region.AreaPixels);
        Assert.AreEqual(1, target.Layers[1].Visuals.Count);
        Assert.AreEqual(0, ((DP.Vision.ContourGeometry)target.Layers[1].Visuals[0].Geometry).Points.Count);
    }

    /// <summary>既有像素中心坐标可逆映射到通用像素边缘坐标。</summary>
    [TestMethod]
    public void SubpixelOffsetIsExplicit()
    {
        var source = new CanvasGeometry(
            Array.Empty<CanvasRegion>(),
            new[] { new CanvasPolyline("x", new[] { new CanvasPoint(1.125, 2.75) }, false) }
        );
        var target = VisionAdapter.ConvertGeometry("f", source);
        var p = ((DP.Vision.ContourGeometry)target.Layers[1].Visuals[0].Geometry).Points[0];
        Assert.AreEqual(1.125, p.X - .5);
        Assert.AreEqual(2.75, p.Y - .5);
        Assert.AreEqual(1.125, source.Contours[0].Points[0].X);
    }

    /// <summary>标签使用边缘坐标，重合发现框只在显示上分组，不删除证据。</summary>
    [TestMethod]
    public void LabelLayersPreserveCoordinatesAndEvidence()
    {
        var bounds = new PixelRect(10, 20, 30, 40);
        var findings = new[]
        {
            new InspectionFinding("a", "first", EInspectionVerdict.Review, bounds),
            new InspectionFinding("b", "second", EInspectionVerdict.Ng, bounds),
        };
        var layers = VisionAdapter.LabelLayers(
            new[] { new InspectionRegion("text", ERegionKind.Text, bounds) },
            findings,
            Array.Empty<CharacterPatch>()
        );
        Assert.AreEqual(3, layers.Count);
        Assert.AreEqual(10d, layers[0].Visuals[0].Geometry.Bounds.X);
        Assert.AreEqual(20d, layers[0].Visuals[0].Geometry.Bounds.Y);
        Assert.AreEqual(1, layers[2].Visuals.Count);
        Assert.AreEqual("F1/F2", layers[2].Visuals[0].Caption);
        Assert.AreEqual(0xFFDC143Cu, layers[2].Visuals[0].Argb);
        Assert.AreEqual(2, findings.Length);
    }

    /// <summary>显示快照与调用方列表分离，可选标题不影响几何保留。</summary>
    [TestMethod]
    public void LabelLayersSnapshotCollections()
    {
        var rois = new System.Collections.Generic.List<InspectionRegion>
        {
            new InspectionRegion("ignore", ERegionKind.Ignore, new PixelRect(1, 2, 3, 4)),
        };
        var layers = VisionAdapter.LabelLayers(
            rois,
            Array.Empty<InspectionFinding>(),
            Array.Empty<CharacterPatch>(),
            false
        );
        rois.Clear();
        Assert.AreEqual(1, layers[0].Visuals.Count);
        Assert.AreEqual("", layers[0].Visuals[0].Caption);
        Assert.AreEqual(0xFF808080u, layers[0].Visuals[0].Argb);
    }

    /// <summary>通用图像生命周期独立于业务快照所有权。</summary>
    [TestMethod]
    public void ImageConversionOwnsPixels()
    {
        var source = new ImageFrame(1, 1, EImagePixelFormat.Gray8, new byte[] { 12 });
        using var image = VisionAdapter.CopyImage(source);
        using var retained = image.Retain();
        image.Dispose();
        var bytes = new byte[1];
        retained.CopyTo(0, bytes, 0, 1);
        Assert.AreEqual((byte)12, bytes[0]);
        Assert.AreEqual(DP.Vision.EPixelLayout.Gray8, retained.Info.Layout);
    }
}
