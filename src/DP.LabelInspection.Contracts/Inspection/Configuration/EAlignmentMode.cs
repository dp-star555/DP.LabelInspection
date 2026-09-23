using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>配准策略；假定已对齐是用户声明，不是自动认证。</summary>
public enum EAlignmentMode
{
    /// <summary>用户声明坐标已对齐。</summary>
    AssumeAligned,

    /// <summary>要求后台实现有界平移配准。</summary>
    Translation,
}
