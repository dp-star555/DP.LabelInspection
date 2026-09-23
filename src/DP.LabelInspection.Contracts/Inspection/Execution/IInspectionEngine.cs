using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>桌面与无界面检测的统一入口，由宿主向UI注入此接口。</summary>
public interface IInspectionEngine
{
    /// <summary>后台实际能力，UI据此准确控制功能可用性。</summary>
    EInspectionCapabilities Capabilities { get; }

    /// <summary>在调用方UI线程之外执行CPU检测工作，不在任务完成前释放相关资源。</summary>
    /// <param name = "request">不可变输入快照。</param>
    /// <param name = "cancellationToken">协作式取消标记；取消异常由调用方处理。</param>
    /// <returns>异步产生完整检测报告的任务。</returns>
    Task<InspectionReport> InspectAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default
    );
}
