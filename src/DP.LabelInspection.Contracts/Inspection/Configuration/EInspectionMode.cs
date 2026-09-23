using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>可用的标签参考模式。</summary>
public enum EInspectionMode
{
    /// <summary>不使用整张标签参考，不能因此无条件放行标签。</summary>
    Free,

    /// <summary>明确的固定区域可以使用图像参考。</summary>
    Template,
}
