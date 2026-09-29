using System;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.Vision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using L = DP.LabelInspection.Contracts;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 后台入口的输入契约：只接受携带Vision原图租约的请求。
/// 旧快照请求必须在这里明确失败，而不是在ROI阶段靠空值兜底整帧转换——
/// 那样会复制整帧、并掩盖像素真正来自哪里。
/// </summary>
[TestClass]
public sealed class OpenSessionInputTests
{
    private const int Width = 32;
    private const int Height = 16;
    private const int ByteLength = Width * Height;

    /// <summary>带一个要求规则质检的空白区，与第1步验收用例保持同一形态。</summary>
    private static InspectionRecipe Recipe()
    {
        var region = new InspectionRegion("roi", ERegionKind.Blank, new PixelRect(0, 0, 16, 16))
            .WithTasks(new RoiInspectionTasks(readData: false, checkQuality: true));
        return new InspectionRecipe(
            "input",
            Width,
            Height,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[] { region },
            new InspectionOptions(tolerancePixels: 0)
        );
    }

    /// <summary>旧快照请求必须在入口被拒绝，且错误信息点名缺失的是Vision租约。</summary>
    [TestMethod]
    public void SnapshotRequestIsRefusedWithExplicitMessage()
    {
        using var backend = new OpenCvInspectionBackend();
        // 标签快照不可释放（自持像素），因此只让请求走 using。
        var snapshot = new L.ImageFrame(Width, Height, EImagePixelFormat.Gray8, new byte[ByteLength]);
        using var request = new InspectionRequest(snapshot, Recipe());

        var error = Assert.ThrowsExactly<ArgumentException>(() => backend.OpenSession(request));

        StringAssert.Contains(
            error.Message,
            "Vision image lease",
            "错误信息必须指出请求缺少Vision原图租约，否则调用方无从判断该改用哪个入口。"
        );
    }

    /// <summary>正向对照：携带Vision租约的请求必须被接受，否则上面的用例可能只是"拒绝一切"。</summary>
    [TestMethod]
    public void VisionRequestIsAccepted()
    {
        using var backend = new OpenCvInspectionBackend();
        using var source = VisionImage.CopyFrom(
            new ImageInfo(Width, Height, EPixelLayout.Gray8),
            new byte[ByteLength]
        );
        using var frame = new V.ImageFrame("open-session", source);
        using var request = InspectionRequest.FromVision(frame, Recipe());

        using var session = backend.OpenSession(request);

        Assert.IsNotNull(session, "携带租约的请求必须能开出会话。");
    }
}
