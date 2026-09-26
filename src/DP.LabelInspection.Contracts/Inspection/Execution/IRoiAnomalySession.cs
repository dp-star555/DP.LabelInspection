using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// ROI会话可选实现的局部块异常检测（方法B）；与<see cref = "IRoiInspectionSession.InspectQuality"/>（方法A）相互独立，
/// 不依赖读取结果。会话未实现本接口时，选择了方法B的ROI明确阻断，不能静默跳过。
/// </summary>
public interface IRoiAnomalySession
{
    /// <summary>检查模型绑定、固定版本、模型键及特征实现是否可用，不执行检测。</summary>
    /// <param name = "region">待检查的ROI配置。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>前提检查记录；缺失资源应以NG明确阻断。</returns>
    IReadOnlyList<InspectionFinding> ValidateAnomaly(InspectionRegion region, CancellationToken token);

    /// <summary>在已定位ROI上执行异常检测，返回异常区域、得分摘要及热力图证据。</summary>
    /// <param name = "region">已定位的ROI。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>带明确完成状态的测量；证据中的<see cref = "RegionInspectionResult.Anomaly"/>保存得分与热力图。</returns>
    RoiQualityMeasurement InspectAnomaly(InspectionRegion region, CancellationToken token);
}
