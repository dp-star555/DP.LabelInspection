using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime.Detection;

/// <summary>将中立候选检测器接入标签制作，不包含推理实现。</summary>
public sealed class TextRegionDetector : ITextRegionDetector
{
    private readonly DP.Vision.Algorithms.ITextRegionDetector _algorithm;
    private readonly bool _ownsAlgorithm;

    /// <summary>接收宿主选择的候选算法，并明确所有权。</summary>
    /// <param name = "algorithm">宿主选择的中立文本候选检测器。</param>
    /// <param name = "ownsAlgorithm">是否转移算法释放责任，默认false表示仅借用。</param>
    public TextRegionDetector(DP.Vision.Algorithms.ITextRegionDetector algorithm, bool ownsAlgorithm = false)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        _ownsAlgorithm = ownsAlgorithm;
    }

    /// <inheritdoc/>
    public string ModelIdentity => _algorithm.ModelIdentity;

    /// <inheritdoc/>
    public IReadOnlyList<PixelRect> Detect(ImageFrame frame, CancellationToken token)
    {
        using var image = Bridge.ToVision(frame);
        return _algorithm.Detect(image, token).Select(Bridge.ToLabel).ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsAlgorithm)
        {
            _algorithm.Dispose();
        }
    }
}
