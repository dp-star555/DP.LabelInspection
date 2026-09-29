using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class RoiWorkflowTests
{
    /// <summary>读取阶段短暂占用并记录同时执行的ROI数及各ROI读取次数；本身不声明可并发。</summary>
    private class OverlapProbe : IInspectionBackend, IRoiInspectionSession
    {
        private int _active;
        internal int MaximumActive;
        internal readonly ConcurrentDictionary<string, int> Reads = new ConcurrentDictionary<string, int>();
        public string Name => "overlap probe";
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

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token)
        {
            int now = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = MaximumActive) < now && Interlocked.CompareExchange(ref MaximumActive, now, seen) != seen) { }
            Thread.Sleep(40);
            Interlocked.Decrement(ref _active);
            Reads.AddOrUpdate(region.Name, 1, (_, n) => n + 1);
            return new RegionInspectionResult(
                region.Name,
                Array.Empty<InspectionFinding>(),
                new TextLineRecognition(
                    region.Bounds,
                    "probe",
                    320,
                    48,
                    new[] { new CtcStep(1, .99f) },
                    new[] { new CtcToken(region.Name == "odd" ? "Y" : "X", 0, 1, .99f) }
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
