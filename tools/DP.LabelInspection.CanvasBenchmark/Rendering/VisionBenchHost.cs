using System;
using System.Collections.Generic;
using System.Windows.Forms;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using V = DP.Vision;

namespace DP.LabelInspection.CanvasBenchmark;

internal sealed class VisionBenchHost : IDisposable
{
    internal readonly V.Winform.VisionCanvasControl Control;
    private readonly V.FrameBufferPool _pool;
    private V.IImageSource? _source;
    private IReadOnlyList<V.CanvasLayer> _layers = Array.Empty<V.CanvasLayer>();
    private long _sequence,
        _imageVersion;

    internal VisionBenchHost(int width, int height, bool lod)
    {
        Control = new V.Winform.VisionCanvasControl
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            Options = new V.CanvasOptions(contourLod: lod),
        };
        var info = new V.ImageInfo(width, height, V.PixelLayout.Gray8);
        _pool = new V.FrameBufferPool(info, 2, 2L * info.ByteLength);
    }

    internal void Image(byte[] pixels)
    {
        if (!_pool.TryRent(out var writer))
            throw new InvalidOperationException("Benchmark preview pool exhausted.");
        using (writer)
        {
            writer!.Write(0, pixels, 0, pixels.Length);
            using var image = writer.Publish();
            var next = image.Retain();
            _source?.Dispose();
            _source = next;
            _imageVersion++;
        }
    }

    internal void Geometry(CanvasGeometry geometry) => _layers = VisionAdapter.GeometryLayers(geometry);

    internal void Present()
    {
        string id = "image-" + _imageVersion;
        using var packet = new V.CanvasFrame(id, ++_sequence, _source!, new V.GeometryOverlay(id, _layers));
        Control.Present(packet);
    }

    public void Dispose()
    {
        Control.Dispose();
        _source?.Dispose();
        _pool.Dispose();
    }
}
