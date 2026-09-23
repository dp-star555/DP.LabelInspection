using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Core;

/// <summary>唯一中立CTC解码实现的标签侧委托入口。</summary>
public static class CtcDecoder
{
    /// <summary>解码实际时间步，不将激活区间当作物理图像边界。</summary>
    /// <param name = "steps">最大概率类别观测。</param>
    /// <param name = "dictionary">索引0为空白，其后为模型字符标签。</param>
    /// <returns>复制后的标签侧字符证据。</returns>
    public static IReadOnlyList<CtcToken> Decode(
        IReadOnlyList<CtcStep> steps,
        IReadOnlyList<string> dictionary
    )
    {
        if (steps == null)
        {
            throw new ArgumentNullException(nameof(steps));
        }

        if (dictionary == null || dictionary.Count < 2)
        {
            throw new ArgumentException("CTC dictionary required.", nameof(dictionary));
        }

        var input = steps
            .Select(s =>
                s == null
                    ? throw new ArgumentException("Null CTC step.", nameof(steps))
                    : new A.CtcStep(s.ClassIndex, s.Confidence)
            )
            .ToArray();
        return Array.AsReadOnly(
            A.CtcDecoder.Decode(input, dictionary)
                .Select(t => new CtcToken(t.Text, t.Start, t.End, t.Confidence))
                .ToArray()
        );
    }
}
