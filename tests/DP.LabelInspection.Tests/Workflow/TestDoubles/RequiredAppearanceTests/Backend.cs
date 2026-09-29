using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class RequiredAppearanceTests
{
    /// <summary>质量阶段返回指定证据及完成状态的会话后台；读取阶段返回固定OCR读数。</summary>
    private sealed class Backend(RegionInspectionResult? quality, bool completed) : IInspectionBackend, IRoiInspectionSession
    {
        public string Name => "controlled quality";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.Ocr;
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

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token) =>
            new RegionInspectionResult(
                region.Name,
                Array.Empty<InspectionFinding>(),
                new TextLineRecognition(
                    region.Bounds,
                    "test",
                    320,
                    48,
                    new[] { new CtcStep(1, .99f) },
                    new[] { new CtcToken("AB", 0, 1, .99f) }
                )
            );

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion region,
            RegionInspectionResult reading,
            CancellationToken token
        ) =>
            new RoiQualityMeasurement(
                quality ?? new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>()),
                completed
            );

        public void Dispose() { }
    }
}
