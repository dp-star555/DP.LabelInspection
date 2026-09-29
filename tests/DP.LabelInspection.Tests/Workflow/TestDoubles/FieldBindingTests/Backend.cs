using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class FieldBindingTests
{
    /// <summary>按ROI返回受控实际读数的会话后台：文字ROI返回OCR，码ROI返回指定数量的码。</summary>
    private sealed class Backend(string text = "A", string barcode = "A", int count = 1, double confidence = .99)
        : IInspectionBackend,
            IRoiInspectionSession
    {
        public string Name => "controlled observations";
        public EInspectionCapabilities Capabilities =>
            EInspectionCapabilities.Ocr | EInspectionCapabilities.BarcodeDecode;
        public int OffsetX => 0;
        public int OffsetY => 0;

        public IRoiInspectionSession OpenSession(InspectionRequest request) => this;

        public bool QualityNeedsReading(InspectionRegion region) => false;

        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion region,
            bool readRequired,
            bool qualityRequired,
            CancellationToken token
        ) => Array.Empty<InspectionFinding>();

        public InspectionRegion Locate(InspectionRegion region, CancellationToken token) => region;

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token)
        {
            if (region.Kind == ERegionKind.Barcode)
            {
                return new RegionInspectionResult(
                    region.Name,
                    Array.Empty<InspectionFinding>(),
                    barcodes: Enumerable
                        .Range(0, count)
                        .Select(_ => new BarcodeObservation(barcode, "CODE128", region.Bounds))
                );
            }

            return new RegionInspectionResult(
                region.Name,
                Array.Empty<InspectionFinding>(),
                new TextLineRecognition(
                    region.Bounds,
                    "test",
                    320,
                    48,
                    new[] { new CtcStep(1, (float)confidence) },
                    new[] { new CtcToken(text, 0, 1, (float)confidence) }
                )
            );
        }

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion region,
            RegionInspectionResult reading,
            CancellationToken token
        ) => new RoiQualityMeasurement(new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>()), true);

        public void Dispose() { }
    }
}
