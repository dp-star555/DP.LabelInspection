using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>由宿主串行调用的请求级算法会话；Read中不能夹带质量检查，InspectQuality中不能夹带业务内容比较。</summary>
public interface IRoiInspectionSession : IDisposable
{
    /// <summary>声明质量策略是否依赖真实身份或码结构，即使没有勾选独立数据项目。</summary>
    /// <param name = "region">当前ROI及其选定的质量配置。</param>
    /// <returns>执行质量前是否必须先完成实际读取。</returns>
    bool QualityNeedsReading(InspectionRegion region);

    /// <summary>检查已知前提、能力和资源，不执行OCR、物理分割或质量测量。</summary>
    /// <param name = "region">待检查的ROI配置。</param>
    /// <param name = "readRequired">本轮是否需要读取，已包含质量依赖。</param>
    /// <param name = "qualityRequired">本轮是否要求质量检查。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>前提检查记录；缺失资源应明确阻断，不能当作已通过。</returns>
    IReadOnlyList<InspectionFinding> Validate(
        InspectionRegion region,
        bool readRequired,
        bool qualityRequired,
        CancellationToken token
    );

    /// <summary>执行已配置的定位，返回原图坐标ROI；异常归属于当前ROI，不中止其他独立ROI。</summary>
    /// <param name = "region">定位前的ROI配置。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>定位后的不可变ROI快照，不修改输入配置。</returns>
    InspectionRegion Locate(InspectionRegion region, CancellationToken token);

    /// <summary>只读取实际内容；读取正确不等于印刷质量合格。</summary>
    /// <param name = "region">已通过前提检查并完成定位的ROI。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>保留原始OCR或码数据的区域结果，不用引导值改写实际值。</returns>
    RegionInspectionResult Read(InspectionRegion region, CancellationToken token);

    /// <summary>利用已有读取证据执行质量检查，可返回多个局部缺陷，不重复进行业务内容比较。</summary>
    /// <param name = "region">已定位的ROI及质量配置。</param>
    /// <param name = "reading">本轮已有读取证据；无需读取时为对应空读取结果。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>包含明确完成状态和详细证据的质量测量结果。</returns>
    RoiQualityMeasurement InspectQuality(
        InspectionRegion region,
        RegionInspectionResult reading,
        CancellationToken token
    );

    /// <summary>已成功求得并应用的全局水平整数平移，单位为原图像素，向右为正。</summary>
    int OffsetX { get; }

    /// <summary>已成功求得并应用的全局垂直整数平移，单位为原图像素，向下为正。</summary>
    int OffsetY { get; }
}
