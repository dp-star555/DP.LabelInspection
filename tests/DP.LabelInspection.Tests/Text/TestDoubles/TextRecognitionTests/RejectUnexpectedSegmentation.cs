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
    private sealed class RejectUnexpectedSegmentation : DP.Vision.Algorithms.ICharacterSegmenter
    {
        public DP.Vision.Algorithms.CharacterSegmentation Segment(
            DP.Vision.IImageSource frame,
            DP.Vision.Algorithms.PixelBounds bounds,
            string text,
            CancellationToken token = default
        )
        {
            throw new InvalidOperationException("Unrequested appearance segmentation ran.");
        }

        public DP.Vision.Algorithms.CharacterSegmentation EqualCells(DP.Vision.IImageSource frame, DP.Vision.Algorithms.PixelBounds bounds, string expected)
        {
            throw new InvalidOperationException("Unrequested cells ran.");
        }
    }
}
