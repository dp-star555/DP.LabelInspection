using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class TextRecognitionTests
{
    private sealed class FakeRecognizer : DP.Vision.Algorithms.ITextLineRecognizer
    {
        internal int Calls,
            Disposals;

        public DP.Vision.Algorithms.TextLineRecognition Recognize(
            DP.Vision.IImageSource frame, DP.Vision.Algorithms.PixelBounds bounds, CancellationToken token)
        {
            Calls++;
            return new DP.Vision.Algorithms.TextLineRecognition(
                bounds,
                "test",
                320,
                48,
                new[] { new DP.Vision.Algorithms.CtcStep(1, .9f) },
                new[] { new DP.Vision.Algorithms.CtcToken("A", 0, 1, .9f) }
            );
        }

        public void Dispose()
        {
            Disposals++;
        }
    }
}
