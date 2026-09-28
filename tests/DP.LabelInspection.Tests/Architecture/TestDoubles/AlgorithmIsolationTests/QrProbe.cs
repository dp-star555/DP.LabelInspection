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
    private sealed class QrProbe : IQrQualityInspector
    {
        internal int Calls;
        public bool RequiresDecodedStructure => false;

        public BarcodeQualityResult Inspect(
            IImageSource frame,
            PixelBounds bounds,
            IReadOnlyList<DP.Vision.Algorithms.BarcodeObservation> symbols,
            DP.Vision.Algorithms.BarcodePrintOptions options,
            CancellationToken token = default
        )
        {
            Calls++;
            return new BarcodeQualityResult(true, new[] {
                new QualityFinding("replacement_qr", "Explicit replacement.",
                    EQualityFindingKind.Defect, bounds)
            });
        }
    }
}
