using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime.Codes;

/// <summary>独立托管DP.Vision码读取器的标签侧委托入口。</summary>
public sealed class ZxingBarcodeDecoder : IBarcodeDecoder
{
    private readonly DP.Vision.Algorithms.IBarcodeReader _reader;

    /// <summary>使用已迁移的ZXing实现。</summary>
    public ZxingBarcodeDecoder()
        : this(new DP.Vision.Zxing.ZxingBarcodeDecoder()) { }

    /// <summary>接收宿主拥有的替代实现。</summary>
    /// <param name = "reader">宿主拥有的中立码读取器，仅借用。</param>
    public ZxingBarcodeDecoder(DP.Vision.Algorithms.IBarcodeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <inheritdoc/>
    public IReadOnlyList<BarcodeObservation> Decode(
        ImageFrame frame,
        PixelRect bounds,
        CancellationToken token
    )
    {
        using var image = Bridge.ToVision(frame);
        return _reader
            .Read(image, Bridge.ToVision(bounds), token)
            .Observations.Select(Bridge.ToLabel)
            .ToArray();
    }
}
