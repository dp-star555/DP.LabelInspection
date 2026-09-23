using System;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

/// <summary>仅控制检测任务结束时机，以验证统一源入口的保留与清理责任。</summary>
internal sealed class SourceInspectionEngine : IInspectionEngine
{
    private readonly Func<InspectionRequest, CancellationToken, Task<InspectionReport>> _run;

    /// <summary>注入可控制的检测行为。</summary>
    /// <param name="run">接收独立业务快照的异步行为。</param>
    internal SourceInspectionEngine(Func<InspectionRequest, CancellationToken, Task<InspectionReport>> run)
    {
        _run = run;
    }

    /// <inheritdoc/>
    public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;

    /// <inheritdoc/>
    public Task<InspectionReport> InspectAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        return _run(request, cancellationToken);
    }
}
