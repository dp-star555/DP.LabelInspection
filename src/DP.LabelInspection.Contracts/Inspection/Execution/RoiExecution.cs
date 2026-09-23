using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>单个配置ROI的精确阶段执行记录。</summary>
public sealed class RoiExecution
{
    /// <summary>创建不可变的阶段执行记录。</summary>
    /// <param name = "prerequisites">配置、资源及能力等前提检查状态。</param>
    /// <param name = "data">实际数据或质量依赖的身份读取状态。</param>
    /// <param name = "comparison">与引导值进行严格内容比较的状态。</param>
    /// <param name = "quality">印刷质量检查状态。</param>
    public RoiExecution(
        ERoiStageState prerequisites,
        ERoiStageState data,
        ERoiStageState comparison,
        ERoiStageState quality
    )
    {
        Prerequisites = prerequisites;
        Data = data;
        Comparison = comparison;
        Quality = quality;
    }

    /// <summary>已知配置、资源和能力的前提检查状态。</summary>
    public ERoiStageState Prerequisites { get; }

    /// <summary>数据项目或质量身份依赖所要求的读取状态。</summary>
    public ERoiStageState Data { get; }

    /// <summary>与预期值或业务引导源的比较状态。</summary>
    public ERoiStageState Comparison { get; }

    /// <summary>印刷质量执行状态。</summary>
    public ERoiStageState Quality { get; }
}
