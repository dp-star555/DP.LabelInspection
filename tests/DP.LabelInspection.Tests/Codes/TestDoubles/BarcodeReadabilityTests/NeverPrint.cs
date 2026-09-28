using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class BarcodeReadabilityTests
{
    private sealed class NeverPrint : DP.Vision.Algorithms.IQrQualityInspector
    {
        public bool RequiresDecodedStructure => true;

        public DP.Vision.Algorithms.BarcodeQualityResult Inspect(
            DP.Vision.IImageSource frame,
            DP.Vision.Algorithms.PixelBounds bounds,
            System.Collections.Generic.IReadOnlyList<DP.Vision.Algorithms.BarcodeObservation> symbols,
            DP.Vision.Algorithms.BarcodePrintOptions options,
            CancellationToken token = default
        )
        {
            throw new InvalidOperationException("Quality must not run after failed reading.");
        }
    }
}
