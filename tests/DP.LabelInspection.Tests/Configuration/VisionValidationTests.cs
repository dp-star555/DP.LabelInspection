using System;
using System.Collections.Generic;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using V = DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>验证Vision请求的配置检查不隐式物化旧图像快照。</summary>
[TestClass]
public sealed class VisionValidationTests
{
    /// <summary>尺寸、整图参考和ROI范围检查只需读取Vision元数据。</summary>
    [TestMethod]
    public void ValidationDoesNotCopyVisionPixels()
    {
        using var actual = new Source(16, 16);
        using var reference = new Source(16, 16);
        using var actualFrame = new V.ImageFrame("validation-actual", actual);
        using var referenceFrame = new V.ImageFrame("validation-reference", reference);
        using var backend = new Backend();
        using var engine = new InspectionEngine(backend);
        using (var request = InspectionRequest.FromVision(actualFrame, Recipe(16, 16), referenceFrame))
        {
            _ = engine.Inspect(request);
            Assert.AreEqual(1, backend.Calls);
        }

        using (var invalid = InspectionRequest.FromVision(actualFrame, Recipe(17, 16), referenceFrame))
            Assert.ThrowsExactly<ArgumentException>(() => engine.Inspect(invalid));
        Assert.AreEqual(0, actual.Copies);
        Assert.AreEqual(0, reference.Copies);
    }

    private static InspectionRecipe Recipe(int width, int height) => new InspectionRecipe(
        "validation", width, height, EInspectionMode.Template, EAlignmentMode.AssumeAligned,
        new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(1, 1, 4, 4)) }
    );

    /// <summary>只读元数据的后台：各阶段不访问像素，记录会话次数。</summary>
    private sealed class Backend : IInspectionBackend, IRoiInspectionSession
    {
        public int Calls { get; private set; }
        public string Name => "metadata-only";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;
        public int OffsetX => 0;
        public int OffsetY => 0;

        public IRoiInspectionSession OpenSession(InspectionRequest request)
        {
            Calls++;
            return this;
        }

        public bool QualityNeedsReading(InspectionRegion region) => false;

        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion region,
            bool readRequired,
            bool qualityRequired,
            CancellationToken token
        ) => Array.Empty<InspectionFinding>();

        public InspectionRegion Locate(InspectionRegion region, CancellationToken token) => region;

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token) =>
            new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>());

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion region,
            RegionInspectionResult reading,
            CancellationToken token
        ) => new RoiQualityMeasurement(new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>()), true);

        public void Dispose() { }
    }

    private sealed class Source : V.IImageSource
    {
        private readonly V.ImageInfo _info;
        private readonly Source? _root;
        private bool _disposed;
        public Source(int width, int height) { _info = new V.ImageInfo(width, height, V.EPixelLayout.Gray8); }
        private Source(Source root) { _info = root._info; _root = root._root ?? root; }
        public int Copies => (_root ?? this)._copies;
        private int _copies;
        public V.ImageInfo Info => !_disposed ? _info : throw new ObjectDisposedException(nameof(Source));
        public V.IImageSource Retain() { _ = Info; return new Source(this); }
        public V.IImageSource ReadTile(int level, int x, int y, int size) => throw new InvalidOperationException("No pixel read expected.");
        public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count)
        {
            Interlocked.Increment(ref (_root ?? this)._copies);
            throw new InvalidOperationException("Validation materialized a legacy snapshot.");
        }
        public void Dispose() => _disposed = true;
    }
}
