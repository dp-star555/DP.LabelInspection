using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>包含检测覆盖语义的判定。</summary>
public enum EInspectionVerdict
{
    /// <summary>仅明确覆盖的检查范围通过。</summary>
    Ok,

    /// <summary>需要复核。</summary>
    Review,

    /// <summary>已配置的检查发现缺陷。</summary>
    Ng,
}
