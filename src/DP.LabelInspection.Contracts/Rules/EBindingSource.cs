using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>序数内容约束使用的独立来源。</summary>
public enum EBindingSource
{
    /// <summary>另一ROI的原始观测。</summary>
    Region,

    /// <summary>本次请求任务快照中的外部字段。</summary>
    TaskData,
}
