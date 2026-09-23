using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class BarcodeReadabilityTests
{
    private sealed class NeverPrint : IBarcodePrintInspector, IRoiBarcodeQualityInspector
    {
        public RoiQualityMeasurement InspectQuality(
            ImageFrame frame,
            InspectionRegion region,
            System.Collections.Generic.IReadOnlyList<BarcodeObservation> symbols,
            CancellationToken token
        )
        {
            throw new InvalidOperationException("Quality must not run after failed reading.");
        }

        public System.Collections.Generic.IReadOnlyList<InspectionFinding> Inspect(
            ImageFrame frame,
            PixelRect bounds,
            System.Collections.Generic.IReadOnlyList<BarcodeObservation> symbols,
            BarcodePrintOptions options,
            CancellationToken token
        )
        {
            throw new InvalidOperationException(
                "Unreadable explicit QR must not be sent to the linear fallback."
            );
        }
    }
}
