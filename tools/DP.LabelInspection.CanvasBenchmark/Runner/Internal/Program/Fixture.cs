using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DP.LabelInspection;
using DP.LabelInspection.Contracts;
using HalconDotNet;
using Newtonsoft.Json;

namespace DP.LabelInspection.CanvasBenchmark;

internal static partial class Program
{
    private sealed class Fixture : IDisposable
    {
        internal readonly int Width,
            Height,
            RunCount,
            PointCount,
            ContourCount;
        internal readonly byte[] Pixels;
        internal HObject Image = null!,
            Regions,
            Contours;

        internal Fixture(string name)
        {
            Width =
                name == "image_16mp" ? 4000
                : name == "image_vga" ? 640
                : 2544;
            Height =
                name == "image_16mp" ? 4000
                : name == "image_vga" ? 480
                : 1608;
            RunCount =
                name == "region_100k" ? 100000
                : name == "mixed" ? 4000
                : name == "region_2k" ? 2000
                : 0;
            PointCount =
                name == "xld_100k" ? 100000
                : name == "xld_10k" ? 10000
                : name == "mixed" ? 20000
                : 0;
            ContourCount = PointCount == 0 ? 0 : 20;
            if (
                !new[]
                {
                    "image_vga",
                    "image_4mp",
                    "image_16mp",
                    "region_2k",
                    "region_100k",
                    "xld_10k",
                    "xld_100k",
                    "mixed",
                }.Contains(name)
            )
                throw new ArgumentException("Unknown workload.");
            Pixels = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Pixels[y * Width + x] = (byte)(50 + ((x / 32 + y / 32) % 2) * 30);
            ReplaceImage();
            HOperatorSet.GenEmptyObj(out Regions);
            HOperatorSet.GenEmptyObj(out Contours);
            if (RunCount > 0)
            {
                int columns = (Width - 40) / 8;
                using var rows = new HTuple(
                    Enumerable.Range(0, RunCount).Select(i => 20 + (i / columns) * 2).ToArray()
                );
                using var starts = new HTuple(
                    Enumerable.Range(0, RunCount).Select(i => 20 + (i % columns) * 8).ToArray()
                );
                using var ends = new HTuple(
                    Enumerable.Range(0, RunCount).Select(i => 23 + (i % columns) * 8).ToArray()
                );
                Regions.Dispose();
                HOperatorSet.GenRegionRuns(out Regions, rows, starts, ends);
            }

            for (int c = 0; c < ContourCount; c++)
            {
                int n = PointCount / ContourCount;
                using var rows = new HTuple(
                    Enumerable
                        .Range(0, n)
                        .Select(i => (c + 1) * Height / (double)(ContourCount + 1) + Math.Sin(i * .03) * 18)
                        .ToArray()
                );
                using var cols = new HTuple(
                    Enumerable.Range(0, n).Select(i => 20 + (Width - 40) * i / (double)(n - 1)).ToArray()
                );
                HOperatorSet.GenContourPolygonXld(out var contour, rows, cols);
                using (contour)
                {
                    HOperatorSet.ConcatObj(Contours, contour, out var next);
                    Contours.Dispose();
                    Contours = next;
                }
            }
        }

        internal void ReplaceImage()
        {
            var pin = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
            try
            {
                HOperatorSet.GenImage1(out var next, "byte", Width, Height, pin.AddrOfPinnedObject());
                Image?.Dispose();
                Image = next;
            }
            finally
            {
                pin.Free();
            }
        }

        internal CanvasGeometry Extract()
        {
            var regions = new List<CanvasRegion>();
            var contours = new List<CanvasPolyline>();
            if (RunCount > 0)
            {
                HOperatorSet.GetRegionRuns(Regions, out var rows, out var starts, out var ends);
                using (rows)
                using (starts)
                using (ends)
                    regions.Add(
                        new CanvasRegion(
                            "r",
                            Enumerable
                                .Range(0, rows.Length)
                                .Select(i => new CanvasRun(rows[i].I, starts[i].I, ends[i].I + 1)),
                            0xFF33CC66
                        )
                    );
            }

            for (int c = 1; c <= ContourCount; c++)
            {
                HOperatorSet.SelectObj(Contours, out var single, c);
                using (single)
                {
                    HOperatorSet.GetContourXld(single, out var rows, out var cols);
                    using (rows)
                    using (cols)
                        contours.Add(
                            new CanvasPolyline(
                                "c" + c,
                                Enumerable
                                    .Range(0, rows.Length)
                                    .Select(i => new CanvasPoint(cols[i].D, rows[i].D)),
                                false
                            )
                        );
                }
            }

            return new CanvasGeometry(regions, contours);
        }

        public void Dispose()
        {
            Image.Dispose();
            Regions.Dispose();
            Contours.Dispose();
        }
    }
}
