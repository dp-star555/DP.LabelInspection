using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>拥有像素的字符提取任务；不得强制按OCR数量切割或返回CTC激活区间裁图。</summary>
/// <remarks>宿主借出该服务，由引擎串行调用；有状态实现的生命周期由宿主明确管理。</remarks>
public interface ICharacterSegmenter
{
    /// <summary>将待确认身份与物理图像证据关联，或返回显式不确定结果。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">单行文字的原图整数范围。</param>
    /// <param name = "text">真实读取的身份提示，不能强制图像凑齐其数量。</param>
    /// <param name = "token">协作式取消标记。</param>
    CharacterSegmentation Segment(
        ImageFrame frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    );

    /// <summary>提取明确声明的等宽单元，不能从OCR推断此布局。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">明确声明等宽布局的原图范围。</param>
    /// <param name = "expected">调用方明确确认的等格标签序列，不是推断的OCR结果。</param>
    CharacterSegmentation EqualCells(ImageFrame frame, PixelRect bounds, string expected);
}
