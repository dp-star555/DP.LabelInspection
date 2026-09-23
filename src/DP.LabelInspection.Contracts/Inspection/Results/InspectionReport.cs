using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>可移植的最终报告；OK只适用于明确配置并完成的检测范围。</summary>
public sealed class InspectionReport
{
    /// <summary>创建最终报告并生成用于展示的证据分组，不改写原始分析记录。</summary>
    /// <param name = "backend">实际使用的后台名称。</param>
    /// <param name = "verdict">考虑项目覆盖和执行状态后的综合判定。</param>
    /// <param name = "analysis">包含各ROI执行和测量结果的分析记录。</param>
    /// <param name = "findings">全局级诊断证据。</param>
    /// <param name = "elapsedMilliseconds">算法墙钟耗时，单位为毫秒，不含宿主渲染或存储。</param>
    public InspectionReport(
        string backend,
        EInspectionVerdict verdict,
        BackendAnalysis analysis,
        IEnumerable<InspectionFinding> findings,
        double elapsedMilliseconds
    )
    {
        Backend = backend;
        Verdict = verdict;
        Analysis = analysis;
        Findings = new ReadOnlyCollection<InspectionFinding>(findings.ToArray());
        ElapsedMilliseconds = elapsedMilliseconds;
        EvidenceGroups = InspectionEvidenceGroup.Create(Analysis, Findings);
    }

    /// <summary>带编号的展示/导出证据组；不修改原始Analysis与Findings。</summary>
    public IReadOnlyList<InspectionEvidenceGroup> EvidenceGroups { get; }

    /// <summary>实际后台名称。</summary>
    public string Backend { get; }

    /// <summary>包含检测范围及执行覆盖语义的综合判定。</summary>
    public EInspectionVerdict Verdict { get; }

    /// <summary>已应用当前判定策略的区域分析记录。</summary>
    public BackendAnalysis Analysis { get; }

    /// <summary>全局级诊断证据。</summary>
    public IReadOnlyList<InspectionFinding> Findings { get; }

    /// <summary>算法耗时，单位为毫秒，不含宿主渲染与存储。</summary>
    public double ElapsedMilliseconds { get; }
}
