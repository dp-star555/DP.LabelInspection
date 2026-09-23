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
    private sealed class InkProbe : IFixedQualityInspector, IBlankQualityInspector
    {
        internal int FixedCalls,
            BlankCalls;
        internal bool Fractional;
        internal IImageSource? LastInput;

        public InkInspectionResult Inspect(
            IImageSource actual,
            IImageSource reference,
            PointD origin,
            InkInspectionOptions options,
            IImageSource? allowedMask = null,
            CancellationToken token = default
        )
        {
            FixedCalls++;
            return Measurement(actual, origin);
        }

        public InkInspectionResult Inspect(
            IImageSource actual,
            PointD origin,
            InkInspectionOptions options,
            IImageSource? allowedMask = null,
            CancellationToken token = default
        )
        {
            BlankCalls++;
            return Measurement(actual, origin);
        }

        private InkInspectionResult Measurement(IImageSource image, PointD origin)
        {
            LastInput = image;
            return new InkInspectionResult(
                EAlgorithmStatus.Completed,
                "",
                "",
                new[]
                {
                    new InkDefect(
                        "test_ink",
                        new RectD(origin.X + (Fractional ? 2.5 : 2), origin.Y + 2, 1, 1),
                        1
                    ),
                }
            );
        }
    }
}
