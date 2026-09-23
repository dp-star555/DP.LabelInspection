using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>独立PP-OCR预处理实现的标签侧委托入口。</summary>
public sealed class OpenCvTextLinePreprocessor : ITextLinePreprocessor
{
    private readonly DP.Vision.Algorithms.ITextLinePreprocessor _algorithm =
        new DP.Vision.OpenCv.OpenCvTextLinePreprocessor();

    /// <inheritdoc/>
    public TextLineInput Prepare(ImageFrame frame, PixelRect bounds, CancellationToken token)
    {
        using var image = Bridge.ToVision(frame);
        var result = _algorithm.Prepare(image, Bridge.ToVision(bounds), token);
        return new TextLineInput(result.Width, result.ContentWidth, result.CopyValues());
    }
}
