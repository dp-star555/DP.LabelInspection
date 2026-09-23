using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>单个字符的外观证据及精确参考来源。</summary>
public sealed class GlyphInspection
{
    /// <summary>创建外观证据。</summary>
    /// <param name = "character">实际独立字符图块及原图范围。</param>
    /// <param name = "status">已比较、超差、缺参考或身份不确定等状态码。</param>
    /// <param name = "referenceSha256">实际使用参考的精确PNG哈希，未选定时为null。</param>
    /// <param name = "comparison">可选归一化比较证据，未执行时为null。</param>
    public GlyphInspection(
        CharacterPatch character,
        string status,
        string? referenceSha256 = null,
        GlyphComparison? comparison = null
    )
    {
        Character = character;
        Status = status;
        ReferenceSha256 = referenceSha256;
        Comparison = comparison;
    }

    /// <summary>实际独立图块。</summary>
    public CharacterPatch Character { get; }

    /// <summary>状态码：compared已比较、exceeds_threshold超差、missing_template缺参考、uncertain_identity身份不确定或empty_reference空参考。</summary>
    public string Status { get; }

    /// <summary>实际选择的模板哈希。</summary>
    public string? ReferenceSha256 { get; }

    /// <summary>可选的归一化测量结果。</summary>
    public GlyphComparison? Comparison { get; }
}
