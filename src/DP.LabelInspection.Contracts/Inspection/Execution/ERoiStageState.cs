using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>阶段执行状态，与可定位缺陷条数分开表达。</summary>
public enum ERoiStageState
{
    /// <summary>未选择，且其他已选阶段也不依赖此阶段。</summary>
    NotRequested,

    /// <summary>本来需要执行，但被前置阶段失败阻止。</summary>
    NotExecuted,

    /// <summary>完整执行并通过。</summary>
    Passed,

    /// <summary>条件缺失、执行失败、内容不符或发现质量缺陷。</summary>
    Failed,
}
