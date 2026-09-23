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
    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);

    private const int CanvasWidth = 1000,
        CanvasHeight = 700;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length != 3)
                throw new ArgumentException(
                    "Usage: CanvasBenchmark <halcon|unified|vision|vision-lod> <case> <new-output-prefix>"
                );
            string backend = args[0],
                name = args[1],
                output = args[2];
            if (backend != "halcon" && backend != "unified" && backend != "vision" && backend != "vision-lod")
                throw new ArgumentException("Unknown renderer.");
            if (File.Exists(output + ".json"))
                throw new IOException("Output exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var fixture = new Fixture(name);
            var metrics = new List<object>();
            metrics.Add(
                Measure(
                    "extract_neutral_geometry",
                    () =>
                    {
                        var snapshot = fixture.Extract();
                        GC.KeepAlive(snapshot);
                    },
                    12
                )
            );
            var scene = fixture.Extract();
            if (
                scene.Regions.Sum(r => r.Runs.Count) != fixture.RunCount
                || scene.Contours.Sum(c => c.Points.Count) != fixture.PointCount
            )
                throw new InvalidOperationException("Fixture geometry was clipped or dropped.");
            HOperatorSet.GetSystem("version", out var halconVersionTuple);
            string halconVersion;
            using (halconVersionTuple)
                halconVersion = halconVersionTuple.S;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var beforeWindow = Memory();
            var init = Stopwatch.StartNew();
            using var form = new Form
            {
                Text = "Canvas benchmark: " + backend + " / " + name,
                ClientSize = new Size(CanvasWidth, CanvasHeight),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(60, 60),
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                TopMost = true,
                ShowInTaskbar = false,
            };
            DP.LabelInspection.LegacyBenchmark.ImageViewerControl? unified = null;
            HWindowControl? native = null;
            using var vision = backend.StartsWith("vision", StringComparison.Ordinal)
                ? new VisionBenchHost(fixture.Width, fixture.Height, backend == "vision-lod")
                : null;
            if (vision != null)
                form.Controls.Add(vision.Control);
            else if (backend == "unified")
            {
                unified = new DP.LabelInspection.LegacyBenchmark.ImageViewerControl
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                };
                form.Controls.Add(unified);
            }
            else
            {
                native = new HWindowControl { Dock = DockStyle.Fill };
                form.Controls.Add(native);
            }

            form.Show();
            form.Activate();
            form.BringToFront();
            Application.DoEvents();
            if (native != null)
            {
                native.HalconWindow.SetWindowParam("flush", "false");
                native.HalconWindow.SetDraw("fill");
                native.HalconWindow.SetLineWidth(2);
            }

            void View(int index)
            {
                double zoom = index < 0 ? 1 : 1.2 + (index % 12) * .1;
                int dx = index < 0 ? 0 : (int)(Math.Sin(index * .3) * 100),
                    dy = index < 0 ? 0 : (int)(Math.Cos(index * .3) * 70);
                if (vision != null)
                {
                    vision.Control.FitToWindow();
                    if (index >= 0)
                        vision.Control.Viewport.Zoom(
                            zoom,
                            new DP.Vision.PointD(CanvasWidth / 2 + dx, CanvasHeight / 2 + dy)
                        );
                }
                else if (unified != null)
                {
                    unified.FitToWindow();
                    if (index >= 0)
                        unified.ZoomAt((float)zoom, new Point(CanvasWidth / 2 + dx, CanvasHeight / 2 + dy));
                }
                else
                {
                    double scale =
                        Math.Min(
                            (CanvasWidth - 24) / (double)fixture.Width,
                            (CanvasHeight - 24) / (double)fixture.Height
                        ) * zoom;
                    double cx = fixture.Width / 2.0 - (1 - zoom) * dx / scale,
                        cy = fixture.Height / 2.0 - (1 - zoom) * dy / scale;
                    native!.HalconWindow.SetPart(
                        (int)Math.Round(cy - CanvasHeight / (2 * scale)),
                        (int)Math.Round(cx - CanvasWidth / (2 * scale)),
                        (int)Math.Round(cy + (CanvasHeight / 2.0 - 1) / scale),
                        (int)Math.Round(cx + (CanvasWidth / 2.0 - 1) / scale)
                    );
                }
            }

            void Draw()
            {
                if (vision != null)
                    vision.Control.Refresh();
                else if (unified != null)
                    unified.Refresh();
                else
                {
                    var window = native!.HalconWindow;
                    window.ClearWindow();
                    window.DispObj(fixture.Image);
                    if (fixture.RunCount > 0)
                    {
                        window.SetColor("#33cc66");
                        window.DispObj(fixture.Regions);
                    }

                    if (fixture.PointCount > 0)
                    {
                        window.SetColor("#ff3388");
                        window.DispObj(fixture.Contours);
                    }

                    window.FlushBuffer();
                }

                GdiFlush();
            }

            if (vision != null)
            {
                vision.Image(fixture.Pixels);
                vision.Geometry(scene);
                vision.Present();
            }

            if (unified != null)
            {
                unified.SetImage(
                    new ImageFrame(fixture.Width, fixture.Height, ImagePixelFormat.Gray8, fixture.Pixels)
                );
                unified.SetGeometry(scene);
            }

            View(-1);
            Draw();
            DwmFlush();
            init.Stop();
            for (int i = 0; i < 8; i++)
                Draw();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var loaded = Memory();
            // 捕获控件缓冲区，不捕获无关桌面像素；截图在计时循环之外执行。
            if (native != null)
            {
                using var image = native.HalconWindow.DumpWindowImage();
                image.WriteImage("png", 0, output + ".png");
            }
            else
            {
                using var bitmap = new Bitmap(CanvasWidth, CanvasHeight);
                ((Control?)vision?.Control ?? unified!).DrawToBitmap(
                    bitmap,
                    new Rectangle(0, 0, CanvasWidth, CanvasHeight)
                );
                bitmap.Save(output + ".png", ImageFormat.Png);
            }

            metrics.Add(Measure("cached_redraw_submit", Draw, 50));
            int viewIndex = 0;
            metrics.Add(
                Measure(
                    "zoom_pan_submit",
                    () =>
                    {
                        View(viewIndex++);
                        Draw();
                    },
                    50
                )
            );
            View(-1);
            metrics.Add(
                Measure(
                    "scene_refresh_submit",
                    () =>
                    {
                        if (vision != null)
                        {
                            vision.Geometry(fixture.Extract());
                            vision.Present();
                        }
                        else if (unified != null)
                            unified.SetGeometry(fixture.Extract());
                        Draw();
                    },
                    16
                )
            );
            metrics.Add(
                Measure(
                    "image_stream_submit",
                    () =>
                    {
                        fixture.Pixels[0] = (byte)(255 - fixture.Pixels[0]);
                        if (vision != null)
                        {
                            vision.Image(fixture.Pixels);
                            vision.Present();
                        }
                        else if (unified != null)
                        {
                            unified.SetImage(
                                new ImageFrame(
                                    fixture.Width,
                                    fixture.Height,
                                    ImagePixelFormat.Gray8,
                                    fixture.Pixels
                                )
                            );
                            unified.SetGeometry(scene);
                        }
                        else
                            fixture.ReplaceImage();
                        View(-1);
                        Draw();
                    },
                    20
                )
            );
            int dwmStatus = 0;
            metrics.Add(
                Measure(
                    "cached_redraw_dwm_sync",
                    () =>
                    {
                        Draw();
                        dwmStatus = DwmFlush();
                    },
                    30
                )
            );
            var beforeGc = Memory();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var retained = Memory();
            var result = new
            {
                schema = "dp.canvas.benchmark.v1",
                utc = DateTimeOffset.UtcNow,
                backend,
                workload = name,
                halconVersion,
                geometryCountValidated = true,
                framework = Environment.Version.ToString(),
                pointerBits = IntPtr.Size * 8,
                logicalProcessors = Environment.ProcessorCount,
                imageWidth = fixture.Width,
                imageHeight = fixture.Height,
                regionObjects = fixture.RunCount > 0 ? 1 : 0,
                regionRuns = fixture.RunCount,
                contourObjects = fixture.ContourCount,
                contourPoints = fixture.PointCount,
                canvasWidth = CanvasWidth,
                canvasHeight = CanvasHeight,
                initialDisplayAndDwmMs = init.Elapsed.TotalMilliseconds,
                beforeWindow,
                loaded,
                beforeGc,
                retained,
                dwmStatus,
                metrics,
                notes = "DP.Vision modes use Gray8 tiles, a two-slot owned input pool, raster Region display and optional open-contour LOD; unlike old full-image GDI rendering, reduced display levels use nearest sampling. Scene refresh also includes legacy-to-Vision conversion. All processes retain identical HALCON fixture sources. Memory includes HALCON in both; use loaded-minus-beforeWindow for incremental viewer cost. Submit timing is not physical display FPS. Scene refresh includes extraction/path rebuilding only for unified; native consumes already-produced HObjects. Geometry identity is unchanged; stream includes current SetImage clearing/reinstalling overlays.",
            };
            File.WriteAllText(output + ".json", JsonConvert.SerializeObject(result, Formatting.Indented));
            Console.WriteLine(JsonConvert.SerializeObject(result));
            form.Close();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static object Measure(string name, Action action, int iterations)
    {
        for (int i = 0; i < 3; i++)
            action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        int g0 = GC.CollectionCount(0),
            g1 = GC.CollectionCount(1),
            g2 = GC.CollectionCount(2);
        var values = new double[iterations];
        var total = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            values[i] = timer.Elapsed.TotalMilliseconds;
        }

        total.Stop();
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        Array.Sort(values);
        return new
        {
            name,
            iterations,
            medianMs = values[iterations / 2],
            p95Ms = values[(int)Math.Ceiling(iterations * .95) - 1],
            minimumMs = values[0],
            maximumMs = values[iterations - 1],
            meanMs = values.Average(),
            cpuMsPerOperation = cpuMs / iterations,
            cpuOneCorePercent = cpuMs / total.Elapsed.TotalMilliseconds * 100,
            cpuMachinePercent = cpuMs / total.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
            gen0 = GC.CollectionCount(0) - g0,
            gen1 = GC.CollectionCount(1) - g1,
            gen2 = GC.CollectionCount(2) - g2,
        };
    }

    private static object Memory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new
        {
            workingSetMiB = process.WorkingSet64 / 1048576.0,
            privateMiB = process.PrivateMemorySize64 / 1048576.0,
            peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0,
            managedMiB = GC.GetTotalMemory(false) / 1048576.0,
            gdiHandles = GetGuiResources(process.Handle, 0),
            userHandles = GetGuiResources(process.Handle, 1),
        };
    }
}
