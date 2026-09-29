using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>仅供旧质量测试构造标签侧观测；真实产品读码只调用DP.Vision的IBarcodeReader。</summary>
public sealed class BarcodeFixtureReader
{
    /// <summary>借用测试快照，调用Vision读码器并转换待测标签质量策略的输入证据。</summary>
    /// <param name="frame">测试图像。</param>
    /// <param name="bounds">测试范围。</param>
    /// <param name="token">取消。</param>
    /// <returns>标签侧码观测。</returns>
    public IReadOnlyList<BarcodeObservation> Decode(ImageFrame frame, PixelRect bounds, CancellationToken token)
    {
        using var image = Bridge.ToVision(frame);
        return new DP.Vision.Zxing.ZxingBarcodeDecoder()
            .Read(image, bounds, token)
            .Observations.Select(Bridge.ToLabel).ToArray();
    }
}
