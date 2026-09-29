using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class RoiWorkflowTests
{
    /// <summary>同一探针，声明各阶段可被多个ROI并发调用。</summary>
    private sealed class ConcurrentOverlapProbe : OverlapProbe, IConcurrentRoiSession { }
}
