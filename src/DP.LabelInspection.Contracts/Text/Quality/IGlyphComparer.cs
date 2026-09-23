using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>独立参考外观比较任务，仅使用独立像素与可移植证据。</summary>
/// <remarks>评分和容差需按具体后台标定；替换实现不能证明数值等价。</remarks>
public interface IGlyphComparer
{
    /// <summary>比较一个独立字符图块；阈值及容差遵循所选实现声明的语义。</summary>
    /// <param name = "actual">不可变的实际独立字符图块。</param>
    /// <param name = "reference">固定版本中明确选定的独立字符参考。</param>
    /// <param name = "threshold">固定二值化灰度阈值，墨迹灰度严格低于该值。</param>
    /// <param name = "tolerance">归一化像素容差半径，0仍保留归一化及对齐步骤。</param>
    GlyphComparison Compare(
        ImageFrame actual,
        GlyphReference reference,
        int threshold = 160,
        int tolerance = 2
    );
}
