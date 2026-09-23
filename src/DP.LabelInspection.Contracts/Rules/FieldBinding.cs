using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>将目标原始观测与另一ROI或本次业务字段比较，不改写观测。</summary>
public sealed class FieldBinding
{
    /// <summary>创建约束；根据来源类型，键表示区域名称或任务数据键。</summary>
    /// <param name = "target">目标ROI名称。</param>
    /// <param name = "source">引导来源类型：另一ROI或本轮任务字段。</param>
    /// <param name = "key">根据来源类型解释为源ROI名称或任务字段键。</param>
    public FieldBinding(string target, EBindingSource source, string key)
    {
        if (
            string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(key)
            || target.Length > 200
            || key.Length > 200
            || !Enum.IsDefined(typeof(EBindingSource), source)
        )
        {
            throw new ArgumentException("Invalid field binding.");
        }

        if (source == EBindingSource.Region && target == key)
        {
            throw new ArgumentException("Cannot bind a region to itself.");
        }

        Target = target;
        Source = source;
        Key = key;
    }

    /// <summary>目标ROI名称。</summary>
    public string Target { get; }

    /// <summary>来源类型。</summary>
    public EBindingSource Source { get; }

    /// <summary>源区域名称或业务字段键。</summary>
    public string Key { get; }
}
