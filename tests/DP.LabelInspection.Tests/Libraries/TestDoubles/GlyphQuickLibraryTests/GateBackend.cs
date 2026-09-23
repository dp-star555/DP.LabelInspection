using System;
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
    private sealed class GateBackend : IInspectionBackend, IGlyphCandidateBackend
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

        public BackendAnalysis Analyze(InspectionRequest request, CancellationToken cancellationToken)
        {
            Work();
            return new BackendAnalysis(255, 200, Array.Empty<RegionInspectionResult>());
        }

        public GlyphCandidateExtraction ExtractGlyphCandidates(
            ImageFrame frame,
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
