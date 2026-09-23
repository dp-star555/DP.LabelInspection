using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>高层原生算法适配契约；不在此暴露Mat、HObject、Bitmap或UI类型。</summary>
public interface IInspectionBackend : IDisposable
{
    /// <summary>用于诊断的实际实现名称。</summary>
    string Name { get; }

    /// <summary>实际已实现的检查能力，不包含未来计划支持的功能。</summary>
    EInspectionCapabilities Capabilities { get; }

    /// <summary>同步分析经过校验的请求；同一引擎串行调用其后台。</summary>
    /// <param name = "request">不可变输入快照。</param>
    /// <param name = "cancellationToken">在原生调用之间检查的协作式取消标记。</param>
    /// <returns>测量与区域证据；错误不能转换成OK。</returns>
    BackendAnalysis Analyze(InspectionRequest request, CancellationToken cancellationToken);
}
