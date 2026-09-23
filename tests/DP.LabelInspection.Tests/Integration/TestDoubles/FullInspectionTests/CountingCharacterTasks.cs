using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

public sealed partial class FullInspectionTests
{
    private sealed class CountingCharacterTasks : ICharacterSegmenter, IGlyphComparer
    {
        public int SegmentCalls,
            CompareCalls;

        public CharacterSegmentation Segment(
            ImageFrame frame,
            PixelRect bounds,
            string text,
            CancellationToken token = default
        )
        {
            SegmentCalls++;
            return new CharacterSegmenter().Segment(frame, bounds, text, token);
        }

        public CharacterSegmentation EqualCells(ImageFrame frame, PixelRect bounds, string expected)
        {
            SegmentCalls++;
            return new CharacterSegmenter().EqualCells(frame, bounds, expected);
        }

        public GlyphComparison Compare(
            ImageFrame actual,
            GlyphReference reference,
            int threshold = 160,
            int tolerance = 2
        )
        {
            CompareCalls++;
            return new GlyphComparer().Compare(actual, reference, threshold, tolerance);
        }
    }
}
