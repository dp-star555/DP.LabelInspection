using System;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>独立物理分割实现的标签侧委托入口。</summary>
public sealed class CharacterSegmenter : ICharacterSegmenter, IGlyphCandidateSegmenter
{
    private readonly DP.Vision.Algorithms.ICharacterSegmenter _algorithm;
    internal DP.Vision.Algorithms.ICharacterSegmenter Algorithm => _algorithm;

    /// <summary>使用已迁移的OpenCV实现。</summary>
    public CharacterSegmenter()
        : this(new DP.Vision.OpenCv.OpenCvCharacterSegmenter()) { }

    /// <summary>接收宿主拥有的替代实现。</summary>
    /// <param name = "algorithm">宿主拥有的中立字符分割实现，不由适配器释放。</param>
    public CharacterSegmenter(DP.Vision.Algorithms.ICharacterSegmenter algorithm)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <inheritdoc/>
    public CharacterSegmentation Segment(
        ImageFrame frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    )
    {
        using var image = Bridge.ToVision(frame);
        using var result = _algorithm.Segment(image, Bridge.ToVision(bounds), text, token);
        return Bridge.ToLabel(result);
    }

    /// <inheritdoc/>
    public CharacterSegmentation EqualCells(ImageFrame frame, PixelRect bounds, string expected)
    {
        using var image = Bridge.ToVision(frame);
        using var result = _algorithm.EqualCells(image, Bridge.ToVision(bounds), expected);
        return Bridge.ToLabel(result);
    }

    /// <inheritdoc/>
    public CharacterSegmentation SegmentCandidates(
        ImageFrame frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    )
    {
        using var image = Bridge.ToVision(frame);
        using var result = _algorithm is DP.Vision.Algorithms.IGlyphCandidateSegmenter candidate
            ? candidate.SegmentCandidates(image, Bridge.ToVision(bounds), text, token)
            : _algorithm.Segment(image, Bridge.ToVision(bounds), text, token);
        return Bridge.ToLabel(result);
    }
}
