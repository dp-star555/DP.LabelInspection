using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class GlyphQuickLibraryTests
{
    private sealed class GateBackend : IInspectionBackend, IGlyphCandidateBackend, IRoiInspectionSession
    {
        private int _active;
        internal bool Overlap;
        public string Name => "gate";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;

        private void Work()
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                Overlap = true;
            }

            Thread.Sleep(10);
            Interlocked.Decrement(ref _active);
        }

        public IRoiInspectionSession OpenSession(InspectionRequest request)
        {
            // 检测在打开会话时占用执行关卡，与候选提取互斥。
            Work();
            return this;
        }

        public int OffsetX => 0;
        public int OffsetY => 0;

        public bool QualityNeedsReading(InspectionRegion region) => false;

        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion region,
            bool readRequired,
            bool qualityRequired,
            CancellationToken token
        ) => Array.Empty<InspectionFinding>();

        public InspectionRegion Locate(InspectionRegion region, CancellationToken token) => region;

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token) =>
            new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>());

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion region,
            RegionInspectionResult reading,
            CancellationToken token
        ) => new RoiQualityMeasurement(new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>()), true);

        public GlyphCandidateExtraction ExtractGlyphCandidates(
            DP.Vision.IImageSource frame,
            PixelRect bounds,
            string? confirmedText,
            CancellationToken token
        )
        {
            Work();
            return new GlyphCandidateExtraction(
                null,
                confirmedText,
                new CharacterSegmentation("uncertain", "test", "test", 0, Array.Empty<CharacterPatch>())
            );
        }

        public void Dispose() { }
    }
}
