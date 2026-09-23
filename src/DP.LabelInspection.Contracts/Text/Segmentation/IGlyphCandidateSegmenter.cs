using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>可选的参考制作分割，可返回待复核候选，不代表生产验收通过。</summary>
public interface IGlyphCandidateSegmenter
{
    /// <summary>提供有图像依据的切分候选供人工复核，不强凑数量或删除源墨迹。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">用于参考制作的单行原图范围。</param>
    /// <param name = "text">候选标签提示，不构成强制切分依据。</param>
    /// <param name = "token">协作式取消标记。</param>
    CharacterSegmentation SegmentCandidates(
        ImageFrame frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    );
}
