using System;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.Vision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 第1步的两条验收点：Vision请求跑完检测不物化惰性整帧快照；请求释放后不再有租约被后台任务占用。
/// 两条都用真实的OpenCv后台和真实的Vision租约，不用替身后台，避免"测试测的是假对象"。
/// </summary>
[TestClass]
public sealed class VisionLeaseLifecycleTests
{
    private const int Width = 32;
    private const int Height = 16;
    private const int ByteLength = Width * Height;

    /// <summary>带一个要求规则质检的空白区，质检会真实读取像素。</summary>
    private static InspectionRecipe Recipe()
    {
        var region = new InspectionRegion("roi", ERegionKind.Blank, new PixelRect(0, 0, 16, 16))
            .WithTasks(new RoiInspectionTasks(readData: false, checkQuality: true));
        return new InspectionRecipe(
            "lease",
            Width,
            Height,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[] { region },
            new InspectionOptions(tolerancePixels: 0)
        );
    }

    /// <summary>验收点1：真实后台全程只按行读取租约，不出现整帧复制。</summary>
    [TestMethod]
    public void RealBackendReadsThroughLeaseWithoutMaterializingSnapshot()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var probe = new LeaseProbe(VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), Gradient()));
        using var actual = new V.ImageFrame("lease-actual", probe);
        using (var request = InspectionRequest.FromVision(actual, Recipe()))
        {
            var report = engine.Inspect(request);

            Assert.AreEqual(1, report.Analysis.Regions.Count, "后台必须真的跑完这一个ROI。");
            Assert.IsTrue(probe.RowCopies > 0, "运行期必须真的通过租约读到像素，否则本用例只是空转。");
            Assert.AreEqual(0, probe.WholeFrameCopies, "运行期不得物化整帧惰性快照。");
        }
    }

    /// <summary>验收点2：请求持有租约直到释放，释放后不残留任何租约，且不能再取用。</summary>
    [TestMethod]
    public void RequestReleasesLeaseExactlyOnceAndRefusesUseAfterDispose()
    {
        using var probe = new LeaseProbe(VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), Gradient()));
        using var actual = new V.ImageFrame("lease-actual", probe);
        Assert.AreEqual(2, probe.LiveLeases, "测试句柄与帧各持一份租约。");

        var request = InspectionRequest.FromVision(actual, Recipe());
        Assert.AreEqual(3, probe.LiveLeases, "请求必须独立保留输入租约。");
        Assert.IsNotNull(request.VisionSource, "运行期后台可借用请求租约。");

        actual.Dispose();
        Assert.AreEqual(2, probe.LiveLeases, "宿主释放自己的句柄后，请求租约必须仍然有效。");
        Assert.IsNotNull(request.VisionSource);

        request.Dispose();
        Assert.AreEqual(1, probe.LiveLeases, "请求释放后只能剩下测试自己的句柄，不得有后台任务继续占用租约。");
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = request.VisionSource);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = request.VisionReference);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = request.CreateActualSnapshot());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = request.CreateReferenceSnapshot());

        request.Dispose();
        Assert.AreEqual(1, probe.LiveLeases, "重复释放不得多归还租约。");
    }

    /// <summary>梯度像素，保证质检有真实的灰度分布可测。</summary>
    private static byte[] Gradient()
    {
        var pixels = new byte[ByteLength];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 256);
        }

        return pixels;
    }
}
