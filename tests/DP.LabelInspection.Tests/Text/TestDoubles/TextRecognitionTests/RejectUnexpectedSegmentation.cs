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
    private sealed class RejectUnexpectedSegmentation : ICharacterSegmenter
    {
        public CharacterSegmentation Segment(
            ImageFrame frame,
            PixelRect bounds,
            string text,
            CancellationToken token = default
        )
        {
            throw new InvalidOperationException("Unrequested appearance segmentation ran.");
        }

        public CharacterSegmentation EqualCells(ImageFrame frame, PixelRect bounds, string expected)
        {
            throw new InvalidOperationException("Unrequested cells ran.");
        }
    }
}
