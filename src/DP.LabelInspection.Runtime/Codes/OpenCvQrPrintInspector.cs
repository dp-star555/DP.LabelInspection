using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>标签侧QR质量委托入口，实测几何及算法现位于DP.Vision。</summary>
public sealed class OpenCvQrPrintInspector
    : IBarcodePrintInspector,
        IBarcodePrintRequirements,
        IRoiBarcodeQualityInspector
{
    /// <inheritdoc/>
    public bool RequiresReading(EBarcodeKind kind)
    {
        return _algorithm.RequiresDecodedStructure;
    }

    private readonly DP.Vision.Algorithms.IQrQualityInspector _algorithm;

    /// <summary>使用独立OpenCV实现。</summary>
    public OpenCvQrPrintInspector()
        : this(new DP.Vision.OpenCv.OpenCvQrPrintInspector()) { }

    /// <summary>使用借用的替代QR算法。</summary>
    /// <param name = "algorithm">宿主拥有的中立QR质量算法，仅借用。</param>
    public OpenCvQrPrintInspector(DP.Vision.Algorithms.IQrQualityInspector algorithm)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <inheritdoc/>
    public RoiQualityMeasurement InspectQuality(
        ImageFrame frame,
        InspectionRegion region,
        IReadOnlyList<BarcodeObservation> symbols,
        CancellationToken token
    )
    {
        using var image = Bridge.ToVision(frame);
        return InspectQuality(image, region, symbols, token);
    }

    /// <summary>借用Vision原图租约运行QR质量检查，不复制成标签图像快照。</summary>
    /// <param name="frame">借用原图，调用方保持有效。</param>
    /// <param name="region">当前ROI与参数。</param>
    /// <param name="symbols">本轮真实读码结构。</param>
    /// <param name="token">协作式取消。</param>
    /// <returns>标签业务质量证据与显式完成状态。</returns>
    public RoiQualityMeasurement InspectQuality(
        DP.Vision.IImageSource frame,
        InspectionRegion region,
        IReadOnlyList<BarcodeObservation> symbols,
        CancellationToken token
    )
    {
        var result = _algorithm.Inspect(
            frame,
            Bridge.ToVision(region.Bounds),
            symbols.Select(Bridge.ToVision).ToArray(),
            Bridge.ToVision(region.Field.BarcodePrint),
            token
        );
        return new RoiQualityMeasurement(
            new RegionInspectionResult(
                region.Name,
                result.Findings.Select(Bridge.ToLabel),
                barcodes: symbols
            ),
            result.Status == DP.Vision.Algorithms.EAlgorithmStatus.Completed
        );
    }

    /// <inheritdoc/>
    public IReadOnlyList<InspectionFinding> Inspect(
        ImageFrame frame,
        PixelRect bounds,
        IReadOnlyList<BarcodeObservation> symbols,
        BarcodePrintOptions options,
        CancellationToken token
    )
    {
        using var image = Bridge.ToVision(frame);
        return _algorithm
            .Inspect(
                image,
                Bridge.ToVision(bounds),
                symbols.Select(Bridge.ToVision).ToArray(),
                Bridge.ToVision(options),
                token
            )
            .Findings.Select(Bridge.ToLabel)
            .ToArray();
    }
}
