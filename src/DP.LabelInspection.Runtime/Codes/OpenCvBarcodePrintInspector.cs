using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>组合独立选择的一维码和QR质量算法，适配标签契约。</summary>
public sealed class OpenCvBarcodePrintInspector
    : IBarcodePrintInspector,
        IBarcodePrintRequirements,
        IRoiBarcodeQualityInspector
{
    /// <inheritdoc/>
    public bool RequiresReading(EBarcodeKind kind)
    {
        return kind == EBarcodeKind.Auto
            || (
                kind == EBarcodeKind.QrCode
                    ? (
                        _qr is IBarcodePrintRequirements requirements
                            ? requirements.RequiresReading(kind)
                            : true
                    )
                    : _linear.RequiresDecodedStructure
            );
    }

    private readonly IBarcodePrintInspector _qr;
    private readonly DP.Vision.Algorithms.ILinearBarcodeQualityInspector _linear;

    /// <summary>使用已迁移的OpenCV默认实现。</summary>
    public OpenCvBarcodePrintInspector()
        : this(new OpenCvQrPrintInspector()) { }

    /// <summary>保留仅注入QR算法的构造入口。</summary>
    /// <param name = "qrInspector">宿主拥有的QR印刷策略，仅借用。</param>
    public OpenCvBarcodePrintInspector(IBarcodePrintInspector qrInspector)
        : this(qrInspector, new DP.Vision.OpenCv.OpenCvBarcodePrintInspector()) { }

    /// <summary>宿主分别选择各码族实现并拥有其生命周期。</summary>
    /// <param name = "qrInspector">宿主拥有的QR印刷策略，仅借用。</param>
    /// <param name = "linearInspector">宿主拥有的中立一维码质量策略，仅借用。</param>
    public OpenCvBarcodePrintInspector(
        IBarcodePrintInspector qrInspector,
        DP.Vision.Algorithms.ILinearBarcodeQualityInspector linearInspector
    )
    {
        _qr = qrInspector ?? throw new ArgumentNullException(nameof(qrInspector));
        _linear = linearInspector ?? throw new ArgumentNullException(nameof(linearInspector));
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

    /// <summary>借用Vision原图运行码质量策略，只有旧式自定义QR实现才需要复制成历史图像快照。</summary>
    /// <param name="frame">借用的原图租约。</param>
    /// <param name="region">ROI及印刷参数。</param>
    /// <param name="symbols">已有的解码证据。</param>
    /// <param name="token">协作式取消。</param>
    /// <returns>带完成状态的ROI质量证据。</returns>
    public RoiQualityMeasurement InspectQuality(
        DP.Vision.IImageSource frame,
        InspectionRegion region,
        IReadOnlyList<BarcodeObservation> symbols,
        CancellationToken token
    )
    {
        bool qr =
            region.Field.BarcodeType == EBarcodeKind.QrCode
            || region.Field.BarcodeType == EBarcodeKind.Auto
                && symbols.Count == 1
                && symbols[0].Format == "QR_CODE";
        if (qr)
        {
            if (_qr is OpenCvQrPrintInspector visionQr)
                return visionQr.InspectQuality(frame, region, symbols, token);
            if (_qr is IRoiBarcodeQualityInspector structured)
                return structured.InspectQuality(Bridge.ToLabel(frame), region, symbols, token);

            return new RoiQualityMeasurement(
                new RegionInspectionResult(
                    region.Name,
                    new[]
                    {
                        new InspectionFinding(
                            "quality_completion_unavailable",
                            "QR替代实现未提供明确完成状态。",
                            EInspectionVerdict.Ng,
                            region.Bounds
                        ),
                    },
                    barcodes: symbols
                ),
                false
            );
        }

        var result = _linear.Inspect(
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
        if (frame == null || symbols == null || options == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Barcode ROI outside image.");
        }

        token.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            return Array.Empty<InspectionFinding>();
        }

        if (symbols.Count == 1 && symbols[0].Format == "QR_CODE")
        {
            return _qr.Inspect(frame, bounds, symbols, options, token);
        }

        using var image = Bridge.ToVision(frame);
        return _linear
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
