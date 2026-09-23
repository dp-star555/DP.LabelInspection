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
    private sealed class FakeRecognizer : ITextLineRecognizer
    {
        internal int Calls,
            Disposals;

        public TextLineRecognition Recognize(ImageFrame frame, PixelRect bounds, CancellationToken token)
        {
            Calls++;
            return new TextLineRecognition(
                bounds,
                "test",
                320,
                48,
                new[] { new CtcStep(1, .9f) },
                new[] { new CtcToken("A", 0, 1, .9f) }
            );
        }

        public void Dispose()
        {
            Disposals++;
        }
    }
}
