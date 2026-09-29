using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 检测后台契约：按ROI分阶段执行（<see cref = "IRoiWorkflowBackend.OpenSession"/>），由Core调度前提检查、定位、读取、比较与质量阶段。
/// 不在此暴露Mat、HObject、Bitmap或UI类型。旧的整图 <c>Analyze</c> 通道已移除：它绕过逐ROI的前提检查与阻断保证。
/// </summary>
public interface IInspectionBackend : IRoiWorkflowBackend, IDisposable
{
    /// <summary>用于诊断的实际实现名称。</summary>
    string Name { get; }

    /// <summary>实际已实现的检查能力，不包含未来计划支持的功能。</summary>
    EInspectionCapabilities Capabilities { get; }
}
