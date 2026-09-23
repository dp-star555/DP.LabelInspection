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

public sealed partial class RoiWorkflowTests
{
    private sealed class Backend : IInspectionBackend, IRoiWorkflowBackend, IRoiInspectionSession
    {
        internal readonly List<string> Calls = new List<string>();
        internal string? Invalid,
            ThrowAt;
        internal bool Complete = true,
            NeedsRead;
        internal int Defects;
        internal string Value = "A";
        public string Name => "stage spy";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;

        public BackendAnalysis Analyze(InspectionRequest r, CancellationToken t)
        {
            throw new InvalidOperationException("Monolithic Analyze must never run.");
        }

        public IRoiInspectionSession OpenSession(InspectionRequest r)
        {
            return this;
        }

        public bool QualityNeedsReading(InspectionRegion r)
        {
            return NeedsRead;
        }

        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion r,
            bool read,
            bool quality,
            CancellationToken t
        )
        {
            Call(r, "validate");
            return r.Name == Invalid
                ? new[]
                {
                    new InspectionFinding("missing_resource", "missing", EInspectionVerdict.Review, r.Bounds),
                }
                : Array.Empty<InspectionFinding>();
        }

        public InspectionRegion Locate(InspectionRegion r, CancellationToken t)
        {
            Call(r, "locate");
            return r;
        }

        public RegionInspectionResult Read(InspectionRegion r, CancellationToken t)
        {
            Call(r, "read");
            return new RegionInspectionResult(
                r.Name,
                Array.Empty<InspectionFinding>(),
                new TextLineRecognition(
                    r.Bounds,
                    "spy",
                    320,
                    48,
                    new[] { new CtcStep(1, .99f) },
                    new[] { new CtcToken(Value, 0, 1, .99f) }
                )
            );
        }

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion r,
            RegionInspectionResult reading,
            CancellationToken t
        )
        {
            Call(r, "quality");
            return new RoiQualityMeasurement(
                new RegionInspectionResult(
                    r.Name,
                    Enumerable
                        .Range(0, Defects)
                        .Select(i => new InspectionFinding(
                            "defect_" + i,
                            "measured",
                            EInspectionVerdict.Ng,
                            new PixelRect(r.Bounds.X + i, r.Bounds.Y, 1, 1),
                            1
                        ))
                ),
                Complete
            );
        }

        private void Call(InspectionRegion r, string stage)
        {
            Calls.Add(r.Name + ":" + stage);
            if (ThrowAt == r.Name + ":" + stage)
            {
                throw new InvalidOperationException("controlled failure");
            }
        }

        public int OffsetX => 0;
        public int OffsetY => 0;

        public void Dispose() { }
    }
}
