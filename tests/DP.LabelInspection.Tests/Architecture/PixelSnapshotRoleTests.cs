using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 标签像素快照的角色边界：它只把像素当数据保存或传递（报告与字库持久化、界面显示、批量训练等离线入口），
/// 不是检测输入——检测与在线推理一律使用 DP.Vision 的图像租约。本测试把这条定位钉成可执行断言，防止回退。
/// </summary>
[TestClass]
public sealed class PixelSnapshotRoleTests
{
    /// <summary>
    /// 快照自持有像素，因此没有释放语义。一旦它变成 IDisposable，所有持有它的报告、字库、界面与训练资产
    /// 都会立刻变成泄漏点或需要成对管理的生命周期，等于把租约语义混进数据模型。
    /// </summary>
    [TestMethod]
    public void SnapshotOwnsItsPixelsWithoutDisposal()
    {
        Assert.IsFalse(
            typeof(IDisposable).IsAssignableFrom(typeof(PixelSnapshot)),
            "PixelSnapshot 不应实现 IDisposable：它的像素是自持有的独立副本，持有者无需释放。"
        );
    }

    /// <summary>
    /// 检测请求只"产出"快照（显式调用 CreateActualSnapshot / CreateReferenceSnapshot 主动复制），
    /// 绝不"接收"快照作为输入；一旦接收，标签侧通用图像就重新变成了运行时入口。
    /// </summary>
    [TestMethod]
    public void DetectionRequestNeverAcceptsASnapshot()
    {
        var snapshot = typeof(PixelSnapshot);
        var request = typeof(InspectionRequest);

        foreach (var ctor in request.GetConstructors())
        {
            Assert.IsFalse(
                ctor.GetParameters().Any(p => p.ParameterType == snapshot),
                "InspectionRequest 的构造器不应接受 PixelSnapshot：" + ctor
            );
        }

        foreach (var method in request.GetMethods().Where(m => m.DeclaringType == request))
        {
            Assert.IsFalse(
                method.GetParameters().Any(p => p.ParameterType == snapshot),
                "InspectionRequest 的公开方法不应接受 PixelSnapshot：" + method.Name
            );
        }
    }

    /// <summary>检测入口由 Vision 租约构造，而不是由标签快照构造。</summary>
    [TestMethod]
    public void DetectionEntryIsBuiltFromVisionLeases()
    {
        var request = typeof(InspectionRequest);
        var fromVision = request.GetMethods().Where(m => m.Name == "FromVision").ToArray();
        Assert.AreNotEqual(0, fromVision.Length, "InspectionRequest.FromVision 应存在。");
        foreach (var method in fromVision)
        {
            Assert.IsTrue(
                method.GetParameters().Any(p => p.ParameterType == typeof(DP.Vision.ImageFrame)),
                "FromVision 应接受 DP.Vision.ImageFrame 租约：" + method
            );
        }
    }
}
