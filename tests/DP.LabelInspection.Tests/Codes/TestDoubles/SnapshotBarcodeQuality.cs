using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>仅供历史标签报告断言；生产码印刷测量由Vision策略执行。</summary>
internal sealed class SnapshotBarcodeQuality
{
    internal IReadOnlyList<InspectionFinding> Inspect(ImageFrame frame, PixelRect bounds,
        IReadOnlyList<BarcodeObservation> symbols, BarcodePrintOptions options, CancellationToken token)
    {
        if (frame == null || symbols == null || options == null) throw new ArgumentNullException(nameof(frame));
        if (!bounds.Fits(frame)) throw new ArgumentException("Barcode ROI outside image.");
        token.ThrowIfCancellationRequested();
        if (!options.Enabled) return Array.Empty<InspectionFinding>();
        using var source = Bridge.ToVision(frame);
        var result = symbols.Count == 1 && symbols[0].Format == "QR_CODE"
            ? new DP.Vision.OpenCv.OpenCvQrPrintInspector().Inspect(
                source, bounds, symbols.Select(Bridge.ToVision).ToArray(),
                options, token)
            : new DP.Vision.OpenCv.OpenCvBarcodePrintInspector().Inspect(
                source, bounds, symbols.Select(Bridge.ToVision).ToArray(),
                options, token);
        return result.Findings.Select(Bridge.ToLabel).ToArray();
    }
}
