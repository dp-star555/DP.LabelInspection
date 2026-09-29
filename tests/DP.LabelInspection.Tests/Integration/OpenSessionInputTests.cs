using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.Vision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 后台入口的输入契约：只接受携带Vision原图租约的请求。
/// </summary>
/// <remarks>
/// 本类原先还有一条"旧快照请求被拒绝"的用例。删掉 <c>InspectionRequest</c> 的快照构造器之后，
/// 已经无法再构造出不带租约的请求，这条用例变成**编译期不可能**，故随之删除。
/// <c>OpenSession</c> 里的租约检查保留为防御：一旦后续批次把 <c>VisionSource</c> 改回可空，
/// 它会立刻在入口暴露，而不是退化成ROI深处的空引用。
/// </remarks>
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

    /// <summary>携带Vision租约的请求必须能开出会话。</summary>
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
