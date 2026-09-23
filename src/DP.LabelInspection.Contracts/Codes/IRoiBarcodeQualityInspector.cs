using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>带显式完成状态和明确码族配置的分阶段码质量契约。</summary>
public interface IRoiBarcodeQualityInspector
{
    /// <summary>仅执行印刷质量检查，不读取数据或比较预期值。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "region">已定位的ROI及其明确码族和印刷配置。</param>
    /// <param name = "symbols">本轮已有的实际码观测，不是重新生成的标准码图。</param>
    /// <param name = "token">协作式取消标记。</param>
    RoiQualityMeasurement InspectQuality(
        ImageFrame frame,
        InspectionRegion region,
        IReadOnlyList<BarcodeObservation> symbols,
        CancellationToken token
    );
}
