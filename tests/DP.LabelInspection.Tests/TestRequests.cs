using System;
using DP.LabelInspection.Adapter.Vision;
using L = DP.LabelInspection.Contracts;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 以标签快照创建检测请求，形态与宿主入口一致：快照 -> Vision图像 -> FromVision。
/// 参数与旧快照构造器逐一对应，供尚未迁移的用例替换 <c>new InspectionRequest(...)</c>。
/// 转换会复制像素一次（快照到Vision图像），随后请求持有独立租约；
/// 临时包装句柄在本方法内归还，因此调用方沿用原有的 using 写法即可。
/// </summary>
internal static class TestRequests
{
    /// <summary>以标签快照创建Vision租约请求。</summary>
    /// <param name = "actual">独立图像快照。</param>
    /// <param name = "recipe">固定配方。</param>
    /// <param name = "reference">可选参考快照。</param>
    /// <param name = "cycleId">宿主采集周期标识。</param>
    /// <param name = "taskData">本周期不可变数据。</param>
    /// <param name = "frameId">原图身份；不提供时生成独立身份。</param>
    /// <returns>拥有独立Vision租约、由调用方释放的检测请求。</returns>
    internal static L.InspectionRequest FromSnapshot(
        L.ImageFrame actual,
        L.InspectionRecipe recipe,
        L.ImageFrame? reference = null,
        string? cycleId = null,
        L.TaskDataSnapshot? taskData = null,
        string? frameId = null
    )
    {
        using var actualLease = AlgorithmContractAdapter.ToVision(actual);
        using var actualFrame = new V.ImageFrame(frameId ?? NewFrameId(), actualLease);
        using var referenceLease = reference == null ? null : AlgorithmContractAdapter.ToVision(reference);
        using var referenceFrame =
            referenceLease == null ? null : new V.ImageFrame(NewFrameId(), referenceLease);

        // 请求自身会 Retain，因此上面几个包装句柄在此归还后租约仍然有效。
        return L.InspectionRequest.FromVision(actualFrame, recipe, referenceFrame, cycleId, taskData);
    }

    private static string NewFrameId() => Guid.NewGuid().ToString("N");
}
