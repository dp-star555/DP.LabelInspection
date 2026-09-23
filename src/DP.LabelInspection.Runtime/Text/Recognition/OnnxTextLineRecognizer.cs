using System;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime.Recognition;

/// <summary>标签读取边界；模型所有权和推理由DP.Vision.Onnx负责。</summary>
public sealed partial class OnnxTextLineRecognizer : ITextLineRecognizer
{
    private readonly DP.Vision.Onnx.OnnxTextLineRecognizer _algorithm;

    /// <summary>加载精确模型，并保留宿主选择的预处理器。</summary>
    /// <param name = "modelPath">本地PP-OCR识别ONNX模型路径。</param>
    /// <param name = "preprocessor">宿主选择的预处理器，仅借用，不由识别器释放。</param>
    /// <param name = "expectedSha256">可选预期SHA256，不匹配时拒绝加载。</param>
    public OnnxTextLineRecognizer(
        string modelPath,
        ITextLinePreprocessor preprocessor,
        string? expectedSha256 = null
    )
    {
        _algorithm = new DP.Vision.Onnx.OnnxTextLineRecognizer(
            modelPath,
            new Preprocessor(preprocessor ?? throw new ArgumentNullException(nameof(preprocessor))),
            expectedSha256
        );
    }

    /// <summary>实际加载模型字节的标识。</summary>
    public string ModelSha256 => _algorithm.ModelSha256;

    /// <inheritdoc/>
    public TextLineRecognition Recognize(ImageFrame frame, PixelRect bounds, CancellationToken token)
    {
        using var image = Bridge.ToVision(frame);
        return Bridge.ToLabel(_algorithm.Recognize(image, Bridge.ToVision(bounds), token));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _algorithm.Dispose();
    }
}
