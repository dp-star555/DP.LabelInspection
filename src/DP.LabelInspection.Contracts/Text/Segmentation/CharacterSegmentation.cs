using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>保守的物理分割结果；数量一致仅属待确认，不证明身份正确。</summary>
public sealed class CharacterSegmentation
{
    /// <summary>创建分割证据。</summary>
    /// <param name = "status">物理分割状态码，候选或不确定状态不代表正式检测通过。</param>
    /// <param name = "reason">状态原因及测量说明。</param>
    /// <param name = "basis">边界来源或分割依据。</param>
    /// <param name = "physicalCount">实测分组数，包含分隔符。</param>
    /// <param name = "characters">需要移交所有权的字符图块集合，内部复制集合。</param>
    public CharacterSegmentation(
        string status,
        string reason,
        string basis,
        int physicalCount,
        IEnumerable<CharacterPatch> characters
    )
    {
        Status = status;
        Reason = reason;
        Basis = basis;
        PhysicalCount = physicalCount;
        Characters = Array.AsReadOnly(characters.ToArray());
    }

    /// <summary>状态码为provisional待确认、uncertain不确定、unsupported不支持或explicit_cells显式等格；参考制作还可返回review_required待复核。</summary>
    public string Status { get; }

    /// <summary>不确定原因说明。</summary>
    public string Reason { get; }

    /// <summary>实测边界的来源。</summary>
    public string Basis { get; }

    /// <summary>包含分隔符的实测分段数。</summary>
    public int PhysicalCount { get; }

    /// <summary>独立拥有像素的字母数字图块。</summary>
    public IReadOnlyList<CharacterPatch> Characters { get; }
}
