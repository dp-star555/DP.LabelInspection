using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>语义不同的检查类型；文字ROI不是整串文字的外观模板。</summary>
public enum ERegionKind
{
    /// <summary>不变的参考内容。</summary>
    Fixed,

    /// <summary>可变文字及独立字形。</summary>
    Text,

    /// <summary>编码符号。</summary>
    Barcode,

    /// <summary>预期无墨迹的表面。</summary>
    Blank,

    /// <summary>从全部检查中排除的像素。</summary>
    Ignore,
}
