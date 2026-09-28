using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

public sealed partial class FullInspectionTests
{
    private sealed class CountingCharacterTasks : DP.Vision.Algorithms.ICharacterSegmenter, DP.Vision.Algorithms.IGlyphComparer
    {
        public int SegmentCalls,
            CompareCalls;
        private readonly DP.Vision.OpenCv.OpenCvCharacterSegmenter _segmenter = new DP.Vision.OpenCv.OpenCvCharacterSegmenter();

        public DP.Vision.Algorithms.CharacterSegmentation Segment(
            DP.Vision.IImageSource frame,
            DP.Vision.Algorithms.PixelBounds bounds,
            string text,
            CancellationToken token = default
        )
        {
            SegmentCalls++;
            return _segmenter.Segment(frame, bounds, text, token);
        }

        public DP.Vision.Algorithms.CharacterSegmentation EqualCells(
            DP.Vision.IImageSource frame, DP.Vision.Algorithms.PixelBounds bounds, string expected)
        {
            SegmentCalls++;
            return _segmenter.EqualCells(frame, bounds, expected);
        }

        public DP.Vision.Algorithms.GlyphComparisonResult Compare(
            DP.Vision.IImageSource actual,
            DP.Vision.IImageSource reference,
            DP.Vision.Algorithms.GlyphComparisonOptions options,
            CancellationToken token = default
        )
        {
            CompareCalls++;
            return new DP.Vision.OpenCv.OpenCvGlyphComparer().Compare(actual, reference, options, token);
        }
    }
}
