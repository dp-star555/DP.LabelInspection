using System;
using DP.LabelInspection.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>放置：宿主定位给出配方→原图的仿射，请求只在ROI范围内按放置取样，检测仍在配方坐标下进行。</summary>
[TestClass]
public sealed class InspectionPlacementTests
{
    // 原图像素值 = (x + 3y) mod 256，便于核对取样来源。
    private static PixelSnapshot Gradient(int width, int height)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = (byte)((x + 3 * y) % 256);
        return new PixelSnapshot(width, height, EImagePixelFormat.Gray8, pixels);
    }

    private static byte Pixel(V.IImageSource image, int x, int y)
    {
        var pixels = new byte[image.Info.ByteLength];
        image.CopyTo(0, pixels, 0, pixels.Length);
        return pixels[y * image.Info.Width + x];
    }

    private static InspectionRecipe Recipe(int width, int height, PixelRect roi) => new InspectionRecipe("placed", width, height,
        EInspectionMode.Free, EAlignmentMode.AssumeAligned, new[] { new InspectionRegion("blank", ERegionKind.Blank, roi) });

    [TestMethod]
    public void IntegerTranslation_CopiesRoiPixelsWithoutInterpolation_AndLeavesRestEmpty()
    {
        var recipe = Recipe(60, 60, new PixelRect(2, 2, 4, 4));
        using var request = PlacedRequest(Gradient(100, 100), recipe, new InspectionPlacement(1, 0, 5, 0, 1, 7));
        Assert.AreEqual(60, request.ImageWidth);
        Assert.AreEqual(60, request.ImageHeight);
        Assert.AreEqual(100, request.OriginalWidth);
        Assert.AreEqual((byte)((3 + 5 + 3 * (3 + 7)) % 256), Pixel(request.VisionSource, 3, 3));
        // ROI 外扩边距之外不取样。
        Assert.AreEqual((byte)0, Pixel(request.VisionSource, 40, 40));
    }

    [TestMethod]
    public void Rotation_Rectify_SamplesRotatedSource()
    {
        // x' = 20 - y，y' = x（配方 X 轴指向原图 +Y）。
        var placement = new InspectionPlacement(0, -1, 20, 1, 0, 0);
        Assert.AreEqual(90, placement.RotationDegrees, 1e-9);
        using var lease = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(Gradient(40, 40));
        using var actual = new V.ImageFrame("rotated", lease);
        using var label = placement.Rectify(actual, 10, 8);
        // 配方像素 (3,5) 的中心 (3.5,5.5) → 原图 (14.5,3.5)，即像素 (14,3)。
        Assert.AreEqual((byte)((14 + 3 * 3) % 256), Pixel(label.Image, 3, 5));
        Assert.AreEqual("rotated#label", label.FrameId);
    }

    [TestMethod]
    public void RoiPlacedOutsideImage_IsRejected()
    {
        var recipe = Recipe(60, 60, new PixelRect(2, 2, 40, 40));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PlacedRequest(Gradient(50, 50), recipe, new InspectionPlacement(1, 0, 30, 0, 1, 0)).Dispose());
    }

    [TestMethod]
    public void SingularPlacement_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new InspectionPlacement(1, 2, 0, 2, 4, 0));
    }

    private static InspectionRequest PlacedRequest(PixelSnapshot actual, InspectionRecipe recipe, InspectionPlacement placement)
    {
        using var lease = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(actual);
        using var frame = new V.ImageFrame(Guid.NewGuid().ToString("N"), lease);
        return InspectionRequest.FromVision(frame, recipe, placement: placement);
    }
}
