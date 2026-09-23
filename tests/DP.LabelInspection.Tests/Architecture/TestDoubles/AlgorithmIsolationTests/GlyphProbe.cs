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

namespace DP.LabelInspection.Tests;

public sealed partial class AlgorithmIsolationTests
{
    private sealed class GlyphProbe : DP.Vision.Algorithms.IGlyphComparer
    {
        internal int Calls;
        internal GlyphComparisonOptions? Options;
        internal GlyphComparisonResult? LastResult;

        public GlyphComparisonResult Compare(
            IImageSource actual,
            IImageSource reference,
            GlyphComparisonOptions options,
            CancellationToken token = default
        )
        {
            Calls++;
            Options = options;
            return LastResult = new GlyphComparisonResult(
                EAlgorithmStatus.Completed,
                "",
                .125,
                1,
                0,
                actual,
                reference,
                actual
            );
        }
    }
}
