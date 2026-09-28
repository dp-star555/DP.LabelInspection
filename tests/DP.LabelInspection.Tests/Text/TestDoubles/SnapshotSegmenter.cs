using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>仅为仍测试长期保存的标签侧字符证据，把Vision分割结果复制为测试快照。</summary>
internal sealed class SnapshotSegmenter
{
    internal CharacterSegmentation Segment(ImageFrame frame, PixelRect bounds, string text,
        CancellationToken token = default)
    {
        using var source = Bridge.ToVision(frame);
        using var result = new DP.Vision.OpenCv.OpenCvCharacterSegmenter().Segment(
            source, Bridge.ToVision(bounds), text, token);
        return Bridge.ToLabel(result);
    }
}
