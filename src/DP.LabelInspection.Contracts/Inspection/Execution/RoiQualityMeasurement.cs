using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>带明确完成标志的质量证据；发现列表为空不能证明完成。</summary>
public sealed class RoiQualityMeasurement
{
    /// <summary>创建质量测量结果；执行完成的结果仍然可以包含多处缺陷。</summary>
    /// <param name = "evidence">策略返回的完整区域证据。</param>
    /// <param name = "completed">所有所需质量工作是否完成，不表示质量通过。</param>
    public RoiQualityMeasurement(RegionInspectionResult evidence, bool completed)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Completed = completed;
    }

    /// <summary>质量策略返回的完整原始证据。</summary>
    public RegionInspectionResult Evidence { get; }

    /// <summary>所有要求的质量工作是否完整完成。</summary>
    public bool Completed { get; }
}
