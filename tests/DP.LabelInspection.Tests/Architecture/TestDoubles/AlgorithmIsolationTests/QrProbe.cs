using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.Vision;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BarcodeObservation = DP.LabelInspection.Contracts.BarcodeObservation;
using BarcodePrintOptions = DP.LabelInspection.Contracts.BarcodePrintOptions;
using ImageFrame = DP.LabelInspection.Contracts.ImageFrame;

namespace DP.LabelInspection.Tests;

public sealed partial class AlgorithmIsolationTests
{
    private sealed class QrProbe : IBarcodePrintInspector
    {
        internal int Calls;

        public IReadOnlyList<InspectionFinding> Inspect(
            ImageFrame frame,
            PixelRect bounds,
            IReadOnlyList<BarcodeObservation> symbols,
            BarcodePrintOptions options,
            CancellationToken token
        )
        {
            Calls++;
            return new[]
            {
                new InspectionFinding(
                    "replacement_qr",
                    "Explicit replacement.",
                    EInspectionVerdict.Ng,
                    bounds
                ),
            };
        }
    }
}
