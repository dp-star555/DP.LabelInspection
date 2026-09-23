using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using V = DP.Vision;

namespace DP.LabelInspection.Demo.WinForms;

internal static class UiInteractionProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    internal static void VerifyGeometryFile(string path)
    {
        var geometry = Newtonsoft.Json.JsonConvert.DeserializeObject<CanvasGeometry>(
            System.IO.File.ReadAllText(path)
        )!;
        using var form = new Form
        {
            ClientSize = new Size(820, 650),
            Text = "Neutral Region / XLD geometry - no HALCON display runtime",
        };
        using var viewer = new ImageViewerControl { Dock = DockStyle.Fill };
        form.Controls.Add(viewer);
        viewer.SetImage(
            new ImageFrame(
                400,
                300,
                EImagePixelFormat.Gray8,
                Enumerable.Repeat((byte)255, 400 * 300).ToArray()
            )
        );
        viewer.SetGeometry(geometry);
        form.Show();
        viewer.ActualSize();
        var viewport = typeof(ImageViewerControl).GetMethod(
            "Viewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        void CheckPixels(string suffix)
        {
            using var bitmap = new Bitmap(viewer.Width, viewer.Height);
            viewer.DrawToBitmap(bitmap, viewer.ClientRectangle);
            var view = (RectangleF)viewport.Invoke(viewer, null)!;
            Color Pixel(double x, double y)
            {
                return bitmap.GetPixel(
                    (int)Math.Round(view.X + (x + .5) * viewer.ImageScale),
                    (int)Math.Round(view.Y + (y + .5) * viewer.ImageScale)
                );
            }

            if (
                Pixel(25, 25).ToArgb() == Color.White.ToArgb()
                || Pixel(260, 210).ToArgb() == Color.White.ToArgb()
                || Pixel(80, 80).ToArgb() != Color.White.ToArgb()
            )
            {
                throw new InvalidOperationException(
                    "Neutral Region lost filled pixels, island or hole on current canvas."
                );
            }

            var p = geometry.Contours[0].Points[0];
            var color = Pixel(p.X, p.Y);
            if (color.G >= 230)
            {
                throw new InvalidOperationException(
                    "Subpixel XLD stroke was not rendered on current canvas."
                );
            }

            bitmap.Save(path + suffix + ".png", System.Drawing.Imaging.ImageFormat.Png);
        }

        CheckPixels(".fit");
        viewer.ZoomAt(1.5f, new Point(viewer.Width / 2, viewer.Height / 2));
        var center = new Point(viewer.Width / 2, viewer.Height / 2);
        var to = new Point(center.X + 20, center.Y + 10);
        SendMessage(viewer.Handle, 0x0207, new IntPtr(16), Pack(center));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(16), Pack(to));
        SendMessage(viewer.Handle, 0x0208, IntPtr.Zero, Pack(to));
        CheckPixels(".zoom-pan");
        if (
            AppDomain
                .CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name!.StartsWith("halcon", StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new InvalidOperationException("HALCON managed runtime leaked into neutral display.");
        }

        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            foreach (System.Diagnostics.ProcessModule module in process.Modules)
            {
                if (module.ModuleName.StartsWith("halcon", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("HALCON native runtime leaked into neutral display.");
                }
            }
        }

        System.IO.File.WriteAllText(
            path + ".render.txt",
            "PASS: current ImageViewerControl; Region hole/island pixels; subpixel XLD; zoom/pan; HALCON assemblies/modules not loaded."
        );
        form.Close();
    }

    internal static async Task VerifyBarcodeSummary(string screenshot)
    {
        using var form = new Form { ClientSize = new Size(1280, 880) };
        using var control = new LabelInspectionControl();
        form.Controls.Add(control);
        using var backend = new DP.LabelInspection.Runtime.OpenCvInspectionBackend(
            barcode: new DP.LabelInspection.Runtime.Codes.ZxingBarcodeDecoder()
        );
        using var engine = new DP.LabelInspection.Core.InspectionEngine(backend);
        control.AttachEngine(engine);
        var encoded = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.CODE_128,
            Options = new ZXing.Common.EncodingOptions
            {
                Width = 600,
                Height = 180,
                Margin = 20,
            },
        }.Write("GROUP-TEST-A1020");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        for (int y = 80; y < 85; y++)
        {
            for (int x = 0; x < encoded.Width; x++)
            {
                pixels[y * encoded.Width + x] = 255;
            }
        }

        using (
            var source = V.VisionImage.CopyFrom(
                new V.ImageInfo(encoded.Width, encoded.Height, V.EPixelLayout.Gray8),
                pixels
            )
        )
            control.SetActualImage(source);
        control.SetRegions(
            new[]
            {
                new InspectionRegion(
                    "bar",
                    ERegionKind.Barcode,
                    new PixelRect(0, 0, encoded.Width, encoded.Height),
                    field: new FieldSettings(barcodeType: EBarcodeKind.OneDimensional)
                ).WithTasks(new RoiInspectionTasks(false, true)),
            }
        );
        form.Show();
        var report = await control.RunInspectionAsync();
        if (!report.EvidenceGroups.Any(g => g.IsBarcode))
        {
            throw new InvalidOperationException(
                "Barcode scope missing: "
                    + string.Join(
                        ";",
                        report
                            .Analysis.Regions.SelectMany(r => r.Findings)
                            .Concat(report.Findings)
                            .Select(f => f.Code + ":" + f.Message)
                    )
            );
        }

        var group = report.EvidenceGroups.Single(g => g.IsBarcode);
        if (group.Status != "NG" || group.Children.Count < 3)
        {
            throw new InvalidOperationException("Barcode probe did not create multiple actual defects.");
        }

        var evidence = Descendants(control).OfType<ListView>().First();
        if (evidence.Items.Count != report.EvidenceGroups.Count)
        {
            throw new InvalidOperationException("Main evidence still lists individual barcode defects.");
        }

        var viewer = FindViewer(control)!;
        var field = typeof(ImageViewerControl).GetField(
            "_findings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        var overlays = (System.Collections.Generic.IReadOnlyList<InspectionFinding>)field.GetValue(viewer)!;
        if (overlays.Count != report.EvidenceGroups.Count || overlays[0].Code != "barcode_summary")
        {
            throw new InvalidOperationException("Main image still paints individual barcode findings.");
        }

        var item = evidence
            .Items.Cast<ListViewItem>()
            .Single(i =>
                i.Tag is Tuple<string, InspectionFinding> tag && ReferenceEquals(tag.Item2, group.Summary)
            );
        item.Selected = true;
        var comparison = Descendants(control).Single(c => c.Name == "BarcodeComparison");
        var details = Descendants(comparison).OfType<ListView>().Single();
        if (details.Items.Count != group.Children.Count)
        {
            throw new InvalidOperationException("Barcode child evidence was lost in comparison.");
        }

        var marked = Descendants(comparison).OfType<ImageViewerControl>().Single();
        if (
            marked.Name != "BarcodeMarked"
            || ((System.Collections.Generic.IReadOnlyList<InspectionFinding>)field.GetValue(marked)!).Count
                == 0
            || marked.ShowFindingLabels
        )
        {
            throw new InvalidOperationException(
                "Barcode view must contain only the marked image, not a duplicate original pane."
            );
        }

        float beforeScale = marked.ImageScale;
        marked.ZoomAt(2, new Point(marked.Width / 2, marked.Height / 2));
        if (marked.ImageScale < beforeScale * 1.5)
        {
            throw new InvalidOperationException("Barcode defect view cannot zoom.");
        }

        using (var capture = new Bitmap(comparison.Width, comparison.Height))
        {
            comparison.DrawToBitmap(capture, comparison.ClientRectangle);
            capture.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        }

        var center = new Point(marked.Width / 2, marked.Height / 2);
        SendMessage(marked.Handle, 0x0207, new IntPtr(16), Pack(center));
        SendMessage(marked.Handle, 0x0200, new IntPtr(16), Pack(new Point(center.X + 20, center.Y + 10)));
        SendMessage(marked.Handle, 0x0208, IntPtr.Zero, Pack(new Point(center.X + 20, center.Y + 10)));
        if (report.Analysis.Regions[0].Findings.Count != group.Children.Count)
        {
            throw new InvalidOperationException("Viewing comparison mutated underlying findings.");
        }

        var choice = Descendants(control)
            .OfType<ComboBox>()
            .Single(c => c.Items.Contains(EBarcodeKind.QrCode));
        choice.SelectedItem = EBarcodeKind.QrCode;
        control.SetRegions(Array.Empty<InspectionRegion>());
        viewer.FitToWindow();
        float scale = viewer.ImageScale;
        Point Screen(int x, int y)
        {
            return new Point(
                (int)Math.Round((viewer.Width - encoded.Width * scale) / 2 + x * scale),
                (int)Math.Round((viewer.Height - encoded.Height * scale) / 2 + y * scale)
            );
        }

        var a = Screen(20, 20);
        var b = Screen(160, 160);
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(a));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(b));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(b));
        if (control.Regions.Single().Field.BarcodeType != EBarcodeKind.QrCode)
        {
            throw new InvalidOperationException("QR ROI selection did not persist the declared family.");
        }

        form.Close();
    }

    internal static void VerifyCanvasZoom()
    {
        var editorType = typeof(LabelInspectionControl).Assembly.GetType(
            "DP.LabelInspection.RegionEditor+EditableRegion",
            true
        )!;
        var parameterModel = Activator.CreateInstance(
            editorType,
            new object[]
            {
                new InspectionRegion("参数中文验证", ERegionKind.Text, new PixelRect(1, 2, 30, 40), true),
            }
        )!;
        foreach (
            System.ComponentModel.PropertyDescriptor property in System.ComponentModel.TypeDescriptor.GetProperties(
                parameterModel
            )
        )
        {
            if (property.DisplayName == property.Name || string.IsNullOrWhiteSpace(property.Description))
            {
                throw new InvalidOperationException(
                    "ROI parameter lacks Chinese name/help: " + property.Name
                );
            }
        }

        using (var modes = new LabelInspectionControl())
        {
            using (
                var reference = V.VisionImage.CopyFrom(
                    new V.ImageInfo(8, 8, V.EPixelLayout.Gray8),
                    new byte[64]
                )
            )
                modes.SetReferenceImage(reference, true);
            using (
                var actual = V.VisionImage.CopyFrom(
                    new V.ImageInfo(16, 16, V.EPixelLayout.Gray8),
                    new byte[256]
                )
            )
                modes.SetActualImage(actual);
            modes.SetRegions(
                new[]
                {
                    new InspectionRegion(
                        "glyph",
                        ERegionKind.Text,
                        new PixelRect(1, 1, 8, 8),
                        true,
                        new FieldSettings(libraryId: "user-font", libraryRevision: 2)
                    ),
                }
            );
            if (modes.CreateRequest().Recipe.Mode != EInspectionMode.Template)
            {
                throw new InvalidOperationException("Mismatched reference was silently discarded.");
            }

            var free =
                Descendants(modes).OfType<Button>().FirstOrDefault(b => b.Text == "无整图参考（可用单字库）")
                ?? throw new InvalidOperationException(
                    "No visible recovery from stale whole-image reference mode."
                );
            free.PerformClick();
            var request = modes.CreateRequest();
            if (
                request.Reference != null
                || request.Recipe.Mode != EInspectionMode.Free
                || request.Recipe.Regions.Single().Field.LibraryId != "user-font"
            )
            {
                throw new InvalidOperationException(
                    "Reference-mode switch lost glyph binding or kept stale template mode."
                );
            }
        }

        using var form = new Form { ClientSize = new Size(620, 460) };
        using var viewer = new ImageViewerControl { Dock = DockStyle.Fill };
        form.Controls.Add(viewer);
        form.Show();
        viewer.Focus();
        var pixels = Enumerable.Repeat((byte)255, 400 * 300).ToArray();
        for (int y = 150; y < 154; y++)
        {
            for (int x = 200; x < 204; x++)
            {
                pixels[y * 400 + x] = 0;
            }
        }

        viewer.SetImage(new ImageFrame(400, 300, EImagePixelFormat.Gray8, pixels));
        var method = typeof(ImageViewerControl).GetMethod(
            "Viewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        RectangleF View()
        {
            return (RectangleF)method.Invoke(viewer, null)!;
        }

        Point Screen(RectangleF v, float x, float y)
        {
            return new Point(
                (int)Math.Round(v.X + x * v.Width / 400),
                (int)Math.Round(v.Y + y * v.Height / 300)
            );
        }

        int Ink()
        {
            using var bitmap = new Bitmap(viewer.Width, viewer.Height);
            viewer.DrawToBitmap(bitmap, viewer.ClientRectangle);
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.R < 20 && c.G < 20 && c.B < 20)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        int originalInk = Ink();
        if (
            !viewer.RenderingBackend.StartsWith("DP.Vision.Winform", StringComparison.Ordinal)
            || viewer.DisplayPixelLayout != "Gray8"
            || viewer.CachedDisplayPixelBytes <= 0
        )
        {
            throw new InvalidOperationException(
                "Legacy facade is not using the Gray8 tiled DP.Vision renderer."
            );
        }

        var before = View();
        var anchor = Screen(before, 202, 152);
        var screen = viewer.PointToScreen(anchor);
        SendMessage(viewer.Handle, 0x020A, new IntPtr(480 << 16), Pack(screen));
        var zoomed = View();
        if (Ink() < originalInk * 2)
        {
            throw new InvalidOperationException(
                "Wheel changed no visible defect pixels; raster image is not magnified."
            );
        }

        if (zoomed.Width < before.Width * 1.5)
        {
            throw new InvalidOperationException("Mouse wheel did not magnify inspection canvas.");
        }

        if (
            Math.Abs((anchor.X - before.X) / before.Width - (anchor.X - zoomed.X) / zoomed.Width) > .003
            || Math.Abs((anchor.Y - before.Y) / before.Height - (anchor.Y - zoomed.Y) / zoomed.Height) > .003
        )
        {
            throw new InvalidOperationException("Zoom moved the original pixel under the mouse.");
        }

        var to = new Point(anchor.X + 35, anchor.Y + 25);
        SendMessage(viewer.Handle, 0x0207, new IntPtr(16), Pack(anchor));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(16), Pack(to));
        SendMessage(viewer.Handle, 0x0208, IntPtr.Zero, Pack(to));
        var panned = View();
        if (Math.Abs(panned.X - zoomed.X - 35) > 1 || Math.Abs(panned.Y - zoomed.Y - 25) > 1)
        {
            throw new InvalidOperationException("Magnified canvas cannot be panned.");
        }

        bool selected = false;
        viewer.SetOverlays(
            Array.Empty<InspectionRegion>(),
            new[]
            {
                new InspectionFinding(
                    "zoom-test",
                    "local defect",
                    EInspectionVerdict.Ng,
                    new PixelRect(200, 150, 4, 4),
                    16
                ),
            }
        );
        viewer.FindingSelected += (_, e) => selected = e.Index == 0;
        var hit = Screen(panned, 202, 152);
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(hit));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(hit));
        if (!selected)
        {
            throw new InvalidOperationException("Zoom/pan broke finding hit coordinates.");
        }

        viewer.SetOverlays(Array.Empty<InspectionRegion>(), Array.Empty<InspectionFinding>());
        PixelRect? drawn = null;
        viewer.RegionDrawn += (_, e) => drawn = e.Bounds;
        var a = Screen(panned, 170, 130);
        var b = Screen(panned, 185, 140);
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(a));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(b));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(b));
        if (
            !drawn.HasValue
            || Math.Abs(drawn.Value.X - 170) > 1
            || Math.Abs(drawn.Value.Y - 130) > 1
            || Math.Abs(drawn.Value.Width - 15) > 1
        )
        {
            throw new InvalidOperationException("Magnified ROI lost original-pixel geometry.");
        }

        var editable = new InspectionRegion("zoom-roi", ERegionKind.Blank, new PixelRect(170, 130, 15, 10));
        viewer.SetOverlays(new[] { editable }, Array.Empty<InspectionFinding>());
        viewer.EditRegions = true;
        PixelRect? edited = null;
        viewer.RegionEdited += (_, e) => edited = e.Bounds;
        a = Screen(panned, 177, 135);
        b = Screen(panned, 182, 140);
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(a));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(b));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(b));
        if (
            !edited.HasValue
            || Math.Abs(edited.Value.X - 175) > 1
            || Math.Abs(edited.Value.Y - 135) > 1
            || edited.Value.Width != 15
        )
        {
            throw new InvalidOperationException("Zoomed ROI move has wrong image coordinates.");
        }

        SendMessage(viewer.Handle, 0x0100, new IntPtr((int)Keys.Home), IntPtr.Zero);
        var fit = View();
        if (Math.Abs(fit.Width - before.Width) > 1 || Math.Abs(fit.X - before.X) > 1)
        {
            throw new InvalidOperationException("Home did not restore fit-to-window.");
        }

        viewer.ActualSize();
        if (Math.Abs(viewer.ImageScale - 1) > .001)
        {
            throw new InvalidOperationException("1:1 is not original pixel scale.");
        }

        viewer.ZoomAt(3, new Point(100, 100));
        viewer.SetImage(new ImageFrame(400, 300, EImagePixelFormat.Gray8, pixels));
        if (Math.Abs(View().Width - before.Width) > 1)
        {
            throw new InvalidOperationException("Loading another image retained stale pan/zoom.");
        }

        form.Close();
    }

    internal static void VerifyCanvasFit(LabelInspectionControl control)
    {
        var form = control.FindForm()!;
        var original = form.Size;
        var viewer = FindViewer(control)!;
        var originalFont = control.Font;
        using (var snapshot = new Bitmap(viewer.Width, viewer.Height))
        {
            viewer.DrawToBitmap(snapshot, viewer.ClientRectangle);
            if (
                !viewer.RenderingBackend.StartsWith("DP.Vision.Winform", StringComparison.Ordinal)
                || viewer.CachedDisplayPixelBytes <= 0
            )
            {
                throw new InvalidOperationException("Actual workbench did not migrate its renderer.");
            }
        }

        using var largeFont = new Font(originalFont.FontFamily, 13.5f);
        try
        {
            foreach (var font in new[] { originalFont, largeFont })
            {
                foreach (var size in new[] { new Size(900, 700), new Size(1600, 950), new Size(1280, 900) })
                {
                    control.Font = font;
                    form.Size = size;
                    form.PerformLayout();
                    control.PerformLayout();
                    viewer.Parent!.PerformLayout();
                    var bounds = control.RectangleToClient(viewer.RectangleToScreen(viewer.ClientRectangle));
                    if (
                        bounds.Left < 0
                        || bounds.Top < 0
                        || bounds.Right > control.ClientSize.Width
                        || bounds.Bottom > control.ClientSize.Height
                    )
                    {
                        throw new InvalidOperationException(
                            $"Canvas extends outside visible workbench: window={size}, canvas={bounds}, client={control.ClientSize}."
                        );
                    }
                }
            }
        }
        finally
        {
            control.Font = originalFont;
            form.Size = original;
            form.PerformLayout();
            control.PerformLayout();
        }
    }

    internal static void VerifyImageDpiConversion()
    {
        foreach (float dpi in new[] { 72f, 96f, 300f, 600f, 1200f })
        {
            using var bitmap = new Bitmap(80, 60, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            bitmap.SetResolution(dpi, dpi);
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < 80; x++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(70 + x * 2, 20 + y * 3, x + y));
                }
            }

            using var stream = new System.IO.MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            using var loaded = Image.FromStream(stream);
            var frame = DrawingImageConverter.FromImage(loaded);
            var pixels = frame.CopyPixels();
            if (frame.Width != 80 || frame.Height != 60)
            {
                throw new InvalidOperationException("DPI changed pixel dimensions.");
            }

            for (int i = 0; i < 80 * 60; i++)
            {
                var expected = bitmap.GetPixel(i % 80, i / 80);
                if (
                    pixels[i * 3] != expected.B
                    || pixels[i * 3 + 1] != expected.G
                    || pixels[i * 3 + 2] != expected.R
                )
                {
                    throw new InvalidOperationException(
                        $"File conversion rescaled original pixels at DPI={dpi}; pixel={i % 80},{i / 80}; BGR={pixels[i * 3]},{pixels[i * 3 + 1]},{pixels[i * 3 + 2]}."
                    );
                }
            }
        }
    }

    internal static void VerifySmallImageFit(LabelInspectionControl control)
    {
        var form = control.FindForm()!;
        var originalSize = form.Size;
        var originalFrame = control.CreateRequest().Actual;
        var originalRegions = control.Regions;
        var viewer = FindViewer(control)!;
        try
        {
            form.Size = new Size(1700, 1000);
            form.PerformLayout();
            control.PerformLayout();
            viewer.Parent!.PerformLayout();
            viewer.SetOverlays(Array.Empty<InspectionRegion>(), Array.Empty<InspectionFinding>());
            viewer.SetImage(new ImageFrame(2048, 1536, EImagePixelFormat.Gray8, new byte[2048 * 1536]));
            // 大图之后加载小型彩色图，测量实际渲染像素，不只检查视口公式。
            var pixels = new byte[30 * 20 * 3];
            for (int i = 0; i < 30 * 20; i++)
            {
                pixels[i * 3] = 40;
                pixels[i * 3 + 1] = 180;
                pixels[i * 3 + 2] = 70;
            }

            viewer.SetImage(new ImageFrame(30, 20, EImagePixelFormat.Bgr24, pixels));
            using var bitmap = new Bitmap(viewer.Width, viewer.Height);
            viewer.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            float scale = Math.Min((viewer.Width - 24) / 30f, (viewer.Height - 24) / 20f);
            int left = (int)Math.Ceiling((viewer.Width - 30 * scale) / 2),
                top = (int)Math.Ceiling((viewer.Height - 20 * scale) / 2);
            foreach (
                var point in new[]
                {
                    new Point(viewer.Width / 2, viewer.Height / 2),
                    new Point(left + 3, top + 3),
                    new Point((int)(left + 30 * scale) - 4, (int)(top + 20 * scale) - 4),
                }
            )
            {
                var color = bitmap.GetPixel(point.X, point.Y);
                if (color.R != 70 || color.G != 180 || color.B != 40)
                {
                    throw new InvalidOperationException(
                        "Small image is not enlarged and centered after loading into a large window: "
                            + point
                            + " color="
                            + color
                            + " viewer="
                            + viewer.Size
                            + " fit="
                            + new RectangleF(left, top, 30 * scale, 20 * scale)
                    );
                }
            }

            if (bitmap.GetPixel(1, 1).ToArgb() != viewer.BackColor.ToArgb())
            {
                throw new InvalidOperationException("Small image incorrectly anchored to canvas corner.");
            }
        }
        finally
        {
            viewer.SetImage(originalFrame);
            control.SetRegions(originalRegions);
            form.Size = originalSize;
            form.PerformLayout();
            control.PerformLayout();
        }
    }

    internal static void VerifyRoiMapping(LabelInspectionControl control)
    {
        var original = control.Regions;
        var viewer = FindViewer(control) ?? throw new InvalidOperationException("Missing image viewer.");
        const int width = 640,
            height = 250;
        float scale = Math.Min(
            Math.Max(1, viewer.ClientSize.Width - 24) / (float)width,
            Math.Max(1, viewer.ClientSize.Height - 24) / (float)height
        );
        float left = (viewer.ClientSize.Width - width * scale) / 2,
            top = (viewer.ClientSize.Height - height * scale) / 2;
        var from = new Point((int)Math.Round(left + 300 * scale), (int)Math.Round(top + 205 * scale));
        var to = new Point((int)Math.Round(left + 365 * scale), (int)Math.Round(top + 230 * scale));
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(from));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(to));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(to));
        if (control.Regions.Count != original.Count + 1)
        {
            throw new InvalidOperationException("Mouse ROI was not created.");
        }

        var bounds = control.Regions.Last().Bounds;
        if (
            Math.Abs(bounds.X - 300) > 1
            || Math.Abs(bounds.Y - 205) > 1
            || Math.Abs(bounds.Width - 65) > 1
            || Math.Abs(bounds.Height - 25) > 1
        )
        {
            throw new InvalidOperationException(
                "Screen-to-original-pixel ROI mapping is incorrect: " + bounds
            );
        }

        control.SetRegions(original);
        viewer.EditRegions = true;
        Point Screen(int x, int y)
        {
            return new Point((int)Math.Round(left + x * scale), (int)Math.Round(top + y * scale));
        }

        void Drag(Point a, Point b)
        {
            SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(a));
            SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(b));
            SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(b));
        }

        Drag(Screen(40, 40), Screen(45, 44));
        var moved = control.Regions[0];
        if (
            Math.Abs(moved.Bounds.X - 25) > 1
            || Math.Abs(moved.Bounds.Y - 24) > 1
            || moved.Bounds.Width != 390
            || moved.Name != original[0].Name
        )
        {
            throw new InvalidOperationException("ROI movement did not preserve geometry/settings.");
        }

        Drag(
            Screen(moved.Bounds.X + moved.Bounds.Width, moved.Bounds.Y + moved.Bounds.Height),
            Screen(moved.Bounds.X + moved.Bounds.Width + 10, moved.Bounds.Y + moved.Bounds.Height + 8)
        );
        var resized = control.Regions[0];
        if (
            Math.Abs(resized.Bounds.Width - 400) > 1
            || Math.Abs(resized.Bounds.Height - 83) > 1
            || control.Regions.Count != original.Count
        )
        {
            throw new InvalidOperationException("ROI handle resize failed.");
        }

        viewer.EditRegions = false;
        control.SetRegions(original);
    }

    internal static async Task VerifyQuickLibrary(
        IGlyphLibraryManager manager,
        IGlyphCandidateService service,
        string screenshot
    )
    {
        VerifyRegionEditorHelp(screenshot + ".parameters.png");
        using var form = new Form
        {
            Text = "Quick glyph builder - actual OCR and native UI",
            ClientSize = new Size(1120, 800),
        };
        using var page = new GlyphQuickBuilderControl();
        string id = manager.CreateLibrary("quick-builder-probe-" + Guid.NewGuid().ToString("N"));
        page.AttachServices(manager, service, id);
        form.Controls.Add(page);
        form.Show();
        using var bitmap = new Bitmap(300, 90);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var font = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("AB12", font, Brushes.Black, 12, 12);
        }

        var frame = DrawingImageConverter.FromImage(bitmap);
        page.SetImage(frame);
        page.SetRegion(new PixelRect(8, 8, 250, 72));
        await page.ExtractAsync();
        if (page.LastExtraction?.Recognition == null || page.Candidates.Rows.Count == 0)
        {
            throw new InvalidOperationException("Quick builder did not execute real OCR and segmentation.");
        }

        await page.ExtractAsync("AB12");
        if (page.Candidates.Rows.Count != 4)
        {
            throw new InvalidOperationException(
                "Quick builder did not produce four image-owned candidates from the declared line."
            );
        }

        foreach (DataGridViewRow row in page.Candidates.Rows)
        {
            row.Cells["Use"].Value = true;
        }

        var reviewed = Descendants(page)
            .OfType<CheckBox>()
            .Single(c => c.Text.StartsWith("已核对", StringComparison.Ordinal));
        reviewed.Checked = true;
        int revision = page.SaveSelected();
        if (revision != 2 || manager.Load(id, revision).Glyphs.Count != 4)
        {
            throw new InvalidOperationException(
                "Quick builder failed its atomic selected reference publication."
            );
        }

        page.Candidates.Rows[0].Cells["Character"].Value = "C";
        page.Candidates.Rows[0].Cells["Use"].Value = true;
        page.Candidates.Rows[1].Cells["Use"].Value = true;
        reviewed.Checked = true;
        revision = page.SaveSelected();
        if (revision != 3 || !manager.Load(id, revision).Glyphs.ContainsKey("C"))
        {
            throw new InvalidOperationException("An existing B prevented incremental addition of new C.");
        }

        using (var capture = new Bitmap(page.Width, page.Height))
        {
            page.DrawToBitmap(capture, page.ClientRectangle);
            capture.Save(screenshot);
        }

        page.SetImage(frame);
        if (page.Candidates.Rows.Count != 0 || page.LastExtraction != null)
        {
            throw new InvalidOperationException("Image change retained stale reference candidates.");
        }

        var joined = Enumerable.Repeat((byte)255, 130 * 36).ToArray();
        for (int i = 0; i < 8; i++)
        {
            for (int y = 7; y < 29; y++)
            {
                for (int x = 6 + i * 15; x < 16 + i * 15; x++)
                {
                    joined[y * 130 + x] = 0;
                }
            }
        }

        for (int x = 16; x < 21; x++)
        {
            joined[15 * 130 + x] = 0;
        }

        page.SetImage(new ImageFrame(130, 36, EImagePixelFormat.Gray8, joined));
        page.SetRegion(new PixelRect(0, 0, 130, 36));
        await page.ExtractAsync("WF675907");
        if (
            page.Candidates.Rows.Count != 8
            || page.LastExtraction?.Segmentation.Status != "review_required"
            || reviewed.Checked
        )
        {
            throw new InvalidOperationException("Thin-bridge candidates were lost or silently approved.");
        }

        if (
            page
                .Candidates.Rows.Cast<DataGridViewRow>()
                .Any(r => r.Cells["Use"].Value is bool chosen && chosen)
        )
        {
            throw new InvalidOperationException(
                "Touching candidates were auto-selected for reference publication."
            );
        }

        using (var capture = new Bitmap(page.Width, page.Height))
        {
            page.DrawToBitmap(capture, page.ClientRectangle);
            capture.Save(screenshot + ".touching.png");
        }

        page.AttachServices(manager, null, id); // 即使没有OCR或分割服务，也必须支持手动编辑。
        page.SetImage(
            new ImageFrame(32, 16, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)40, 512).ToArray()),
            "manual-first.png"
        );
        var modes = Descendants(page).OfType<ComboBox>().Single(c => c.Items.Contains("手工补画字块"));
        var editor = FindViewer(page)!;
        modes.SelectedItem = "手工补画字块";
        var viewport = typeof(ImageViewerControl).GetMethod(
            "Viewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        Point Screen(int x, int y)
        {
            var view = (RectangleF)viewport.Invoke(editor, null)!;
            return new Point(
                (int)Math.Round(view.X + x * editor.ImageScale),
                (int)Math.Round(view.Y + y * editor.ImageScale)
            );
        }

        var a = Screen(2, 2);
        var b = Screen(22, 14);
        SendMessage(editor.Handle, 0x0201, new IntPtr(1), Pack(a));
        SendMessage(editor.Handle, 0x0200, new IntPtr(1), Pack(b));
        SendMessage(editor.Handle, 0x0202, IntPtr.Zero, Pack(b));
        if (page.Candidates.Rows.Count != 1)
        {
            throw new InvalidOperationException("Native manual crop failed without automatic candidates.");
        }

        modes.SelectedItem = "手动切开（点切线）";
        var cut = Screen(12, 8);
        SendMessage(editor.Handle, 0x0201, new IntPtr(1), Pack(cut));
        SendMessage(editor.Handle, 0x0202, IntPtr.Zero, Pack(cut));
        if (page.Candidates.Rows.Count != 2 || editor.Capture)
        {
            throw new InvalidOperationException(
                "Native explicit split failed or started a stray ROI gesture."
            );
        }

        modes.SelectedItem = "调整候选框";
        var select = Screen(6, 8);
        SendMessage(editor.Handle, 0x0201, new IntPtr(1), Pack(select));
        SendMessage(editor.Handle, 0x0202, IntPtr.Zero, Pack(select));
        var edge = Screen(2, 8);
        var moved = Screen(1, 8);
        reviewed.Checked = true;
        SendMessage(editor.Handle, 0x0201, new IntPtr(1), Pack(edge));
        SendMessage(editor.Handle, 0x0200, new IntPtr(1), Pack(moved));
        SendMessage(editor.Handle, 0x0202, IntPtr.Zero, Pack(moved));
        if (
            ((DP.LabelInspection.Core.GlyphDraftCandidate)page.Candidates.Rows[0].Tag!).Bounds.X != 1
            || reviewed.Checked
        )
        {
            throw new InvalidOperationException("Native manual boundary edit failed or retained approval.");
        }

        Descendants(page).OfType<Button>().Single(c => c.Text == "撤销调整").PerformClick();
        if (((DP.LabelInspection.Core.GlyphDraftCandidate)page.Candidates.Rows[0].Tag!).Bounds.X != 2)
        {
            throw new InvalidOperationException("Manual edit undo failed.");
        }

        Descendants(page).OfType<Button>().Single(c => c.Text == "重做").PerformClick();
        if (((DP.LabelInspection.Core.GlyphDraftCandidate)page.Candidates.Rows[0].Tag!).Bounds.X != 1)
        {
            throw new InvalidOperationException("Manual edit redo failed.");
        }

        page.Candidates.Rows[0].Cells["Character"].Value = "D";
        page.Candidates.Rows[1].Cells["Character"].Value = "E";
        foreach (DataGridViewRow row in page.Candidates.Rows)
        {
            row.Cells["Use"].Value = true;
        }

        reviewed.Checked = true;
        page.StageSelected();
        page.SetImage(
            new ImageFrame(32, 16, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)90, 512).ToArray()),
            "manual-second.png"
        );
        if (page.PendingCount != 2 || page.Candidates.Rows.Count != 0)
        {
            throw new InvalidOperationException("Cross-image staging was discarded.");
        }

        page.AddManualCandidate(new PixelRect(4, 2, 12, 12));
        page.Candidates.Rows[0].Cells["Character"].Value = "F";
        page.Candidates.Rows[0].Cells["Use"].Value = true;
        reviewed.Checked = true;
        page.StageSelected();
        using (var capture = new Bitmap(page.Width, page.Height))
        {
            page.DrawToBitmap(capture, page.ClientRectangle);
            capture.Save(screenshot + ".multi-image.png");
        }

        if (
            page.SavePending() != 4
            || page.PendingCount != 0
            || manager.Load(id, 4).Glyphs.Count != 8
            || manager.Load(id, 4).Glyphs["D"].Image.CopyPixels()[0] != 40
            || manager.Load(id, 4).Glyphs["F"].Image.CopyPixels()[0] != 90
        )
        {
            throw new InvalidOperationException(
                "Multi-image publication lost an old/new character or source pixels."
            );
        }

        form.Close();
        using var inspectionForm = new Form { ClientSize = new Size(1180, 820) };
        using var inspection = new LabelInspectionControl();
        inspectionForm.Controls.Add(inspection);
        inspection.AttachEngine((IInspectionEngine)service);
        using (var source = V.VisionImage.CopyFrom(new V.ImageInfo(130, 36, V.EPixelLayout.Gray8), joined))
            inspection.SetActualImage(source);
        inspection.SetRegions(
            new[]
            {
                new InspectionRegion(
                    "required-glyphs",
                    ERegionKind.Text,
                    new PixelRect(0, 0, 130, 36),
                    true,
                    new FieldSettings(id, 4)
                ),
            }
        );
        inspectionForm.Show();
        var failed = await inspection.RunInspectionAsync();
        var parent = failed.EvidenceGroups.Single(g => g.RegionName == "required-glyphs");
        if (
            parent.Status != "NG"
            || parent.Children.Count < 1
            || parent.BlockingItemCount < 1
            || parent.LocalizedCandidateCount != 0
        )
        {
            throw new InvalidOperationException(
                "Uncompleted comparison was not a single NG ROI with non-defect blocking evidence."
            );
        }

        var evidence = Descendants(inspection).OfType<ListView>().First();
        evidence
            .Items.Cast<ListViewItem>()
            .Single(i =>
                i.Tag is Tuple<string, InspectionFinding> t && ReferenceEquals(t.Item2, parent.Summary)
            )
            .Selected = true;
        var detail = Descendants(inspection).OfType<ListView>().Single(v => v.Name == "RoiEvidenceDetails");
        if (detail.Items.Count != parent.Children.Count)
        {
            throw new InvalidOperationException("ROI parent lost child evidence in native detail view.");
        }

        using (var capture = new Bitmap(inspection.Width, inspection.Height))
        {
            inspection.DrawToBitmap(capture, inspection.ClientRectangle);
            capture.Save(screenshot + ".required-roi.png");
        }

        inspectionForm.Close();
        System.IO.File.WriteAllText(
            screenshot + ".txt",
            "PASS actual OCR candidates; explicit AB12 manual resegmentation; four human-selected patches; one new library revision; source invalidates candidates. Not an OCR accuracy benchmark."
        );
    }

    internal static async Task VerifyLibraryEditor(IGlyphLibraryManager manager, string screenshot)
    {
        using var form = new Form
        {
            Text = "单字模板编辑器回归",
            Width = 1080,
            Height = 760,
        };
        var editor = new GlyphLibraryControl();
        editor.AttachManager(manager);
        form.Controls.Add(editor);
        var head = manager.ListLibraries().First();
        var glyph = manager.Load(head.Id, head.Revision).Glyphs.Values.First();
        editor.SetCandidate(glyph.Image, glyph.Character);
        form.Show();
        await Task.Delay(100);
        var viewer =
            FindViewer(editor) ?? throw new InvalidOperationException("Library crop viewer missing.");
        bool eventRaised = false;
        viewer.RegionDrawn += (_, e) => eventRaised = e.Bounds.Width > 0;
        float scale = Math.Min(
            Math.Max(1, viewer.ClientSize.Width - 24) / (float)glyph.Image.Width,
            Math.Max(1, viewer.ClientSize.Height - 24) / (float)glyph.Image.Height
        );
        float left = (viewer.ClientSize.Width - glyph.Image.Width * scale) / 2,
            top = (viewer.ClientSize.Height - glyph.Image.Height * scale) / 2;
        var a = new Point((int)(left + 2 * scale), (int)(top + 2 * scale));
        var b = new Point(
            (int)(left + (glyph.Image.Width - 2) * scale),
            (int)(top + (glyph.Image.Height - 2) * scale)
        );
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(a));
        SendMessage(viewer.Handle, 0x0200, new IntPtr(1), Pack(b));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(b));
        if (!eventRaised)
        {
            throw new InvalidOperationException("Single-character crop mouse interaction failed.");
        }

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

    internal static async Task VerifyDataBindings(LabelInspectionControl control)
    {
        var request = control.CreateRequest();
        using var bitmap = DrawingImageConverter.ToBitmap(request.Actual);
        var matrix = new ZXing.MultiFormatWriter().encode("A1020", ZXing.BarcodeFormat.QR_CODE, 140, 140);
        for (int y = 0; y < 140; y++)
        {
            for (int x = 0; x < 140; x++)
            {
                bitmap.SetPixel(460 + x, 25 + y, matrix[x, y] ? Color.Black : Color.White);
            }
        }

        var frame = DrawingImageConverter.FromImage(bitmap);
        using var source = AlgorithmContractAdapter.ToVision(frame);
        control.SetActualImage(source, false);
        control.SetRegions(
            request
                .Recipe.Regions.Where(r => r.Kind == ERegionKind.Text)
                .Concat(
                    new[]
                    {
                        new InspectionRegion(
                            "code",
                            ERegionKind.Barcode,
                            new PixelRect(0, 0, frame.Width, frame.Height)
                        ),
                    }
                )
        );
        control.SetBindings(
            new[]
            {
                new FieldBinding("variable-text", DP.LabelInspection.Contracts.EBindingSource.Region, "code"),
                new FieldBinding(
                    "variable-text",
                    DP.LabelInspection.Contracts.EBindingSource.TaskData,
                    "Part"
                ),
                new FieldBinding("code", DP.LabelInspection.Contracts.EBindingSource.TaskData, "Part"),
            }
        );
        var now = DateTimeOffset.UtcNow;
        control.SetTaskData(
            "smoke-cycle",
            new TaskDataSnapshot(
                "smoke-cycle",
                "UI real OCR + QR",
                now,
                now.AddMinutes(5),
                new System.Collections.Generic.Dictionary<string, string> { { "Part", "A1020" } }
            )
        );
        var report = await control.RunInspectionAsync();
        if (report.Analysis.Regions.SelectMany(r => r.Findings).Count(f => f.Code == "binding_match") != 3)
        {
            throw new InvalidOperationException("Real OCR/QR/task-data binding failed.");
        }

        var textResult = report.Analysis.Regions.Single(r => r.RegionName == "variable-text");
        if (
            textResult.Segmentation != null
            || textResult.Findings.Any(f =>
                f.Code == "glyph_library_unbound"
                || f.Code == "no_character_coverage"
                || f.Code == "segmentation_review"
            )
        )
        {
            throw new InvalidOperationException(
                "Content-only binding incorrectly required appearance checks."
            );
        }

        if (report.Analysis.Regions.Single(r => r.RegionName == "variable-text").Recognition?.Text != "A1020")
        {
            throw new InvalidOperationException("Binding rewrote original OCR.");
        }

        control.SetActualImage(source, false);
        if (control.CreateRequest().TaskData != null)
        {
            throw new InvalidOperationException("New image retained preceding task data.");
        }

        report = await control.RunInspectionAsync();
        if (
            report
                .Analysis.Regions.SelectMany(r => r.Findings)
                .Count(f => f.Code == "binding_unavailable" && f.Verdict == EInspectionVerdict.Ng) != 2
        )
        {
            throw new InvalidOperationException("Missing current-cycle data did not fail before reading.");
        }

        control.SetTaskData(
            "smoke-cycle",
            new TaskDataSnapshot(
                "smoke-cycle",
                "UI mismatch test",
                now,
                now.AddMinutes(5),
                new System.Collections.Generic.Dictionary<string, string> { { "Part", "WRONG" } }
            )
        );
        report = await control.RunInspectionAsync();
        if (
            report
                .Analysis.Regions.SelectMany(r => r.Findings)
                .Count(f => f.Code == "binding_mismatch" && f.Verdict == EInspectionVerdict.Ng) != 2
        )
        {
            throw new InvalidOperationException("Agreement between OCR/QR hid wrong external task data.");
        }

        control.SetBindings(Array.Empty<FieldBinding>());
    }

    private static void VerifyRegionEditorHelp(string screenshot)
    {
        var type = typeof(LabelInspectionControl).Assembly.GetType("DP.LabelInspection.RegionEditor", true)!;
        var edit = type.GetMethod(
            "Edit",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        )!;
        Exception? failure = null;
        bool checkedHelp = false;
        using var timer = new Timer { Interval = 80 };
        timer.Tick += (_, __) =>
        {
            var dialog = Application
                .OpenForms.Cast<Form>()
                .FirstOrDefault(f => f.Text.StartsWith("ROI配置", StringComparison.Ordinal));
            if (dialog == null)
            {
                return;
            }

            timer.Stop();
            try
            {
                var grid = Descendants(dialog).OfType<PropertyGrid>().Single();
                var properties = System.ComponentModel.TypeDescriptor.GetProperties(grid.SelectedObject);
                if (properties["BarcodePrintEnabled"] != null)
                {
                    throw new InvalidOperationException(
                        "Obsolete second quality switch remains in the ROI editor."
                    );
                }

                foreach (
                    string name in new[]
                    {
                        "ReadData",
                        "CheckQuality",
                        "SingleLine",
                        "EqualCells",
                        "CheckQrQuietZone",
                        "DetectInkLoss",
                    }
                )
                {
                    var converter = properties[name]!.Converter;
                    if (
                        converter.ConvertToString(true) != "是"
                        || converter.ConvertToString(false) != "否"
                        || !Equals(converter.ConvertFromString("是"), true)
                    )
                    {
                        throw new InvalidOperationException(
                            "Boolean UI values are not Chinese or do not round trip."
                        );
                    }
                }

                if (
                    properties["Kind"]!.Converter.ConvertToString(ERegionKind.Text) != "文字"
                    || !Equals(
                        properties["BarcodeType"]!.Converter.ConvertFromString("二维码（QR）"),
                        EBarcodeKind.QrCode
                    )
                )
                {
                    throw new InvalidOperationException(
                        "Enum labels changed persisted values or remain English."
                    );
                }

                GridItem root = grid.SelectedGridItem;
                while (root.Parent != null)
                {
                    root = root.Parent;
                }

                GridItem? Find(GridItem item)
                {
                    if (item.PropertyDescriptor?.Name == "GlyphTolerance")
                    {
                        return item;
                    }

                    foreach (GridItem child in item.GridItems)
                    {
                        var found = Find(child);
                        if (found != null)
                        {
                            return found;
                        }
                    }

                    return null;
                }

                grid.SelectedGridItem =
                    Find(root) ?? throw new InvalidOperationException("Glyph tolerance property is missing.");
                var help = Descendants(dialog).OfType<TextBox>().Single(c => c.Name == "RoiParameterHelp");
                if (
                    !help.Visible
                    || help.Height < 100
                    || !help.Text.Contains("0关闭")
                    || !help.Text.Contains("5×5")
                    || !help.Text.Contains("不是原图像素")
                )
                {
                    throw new InvalidOperationException("Selected parameter lacks visible actionable help.");
                }

                using (var capture = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(capture, new Rectangle(Point.Empty, dialog.Size));
                    capture.Save(screenshot);
                }

                properties["GlyphTolerance"]!.SetValue(grid.SelectedObject, 0);
                checkedHelp = true;
                Descendants(dialog).OfType<Button>().Single(b => b.Text == "保存配置").PerformClick();
            }
            catch (Exception error)
            {
                failure = error;
                dialog.Close();
            }
        };
        timer.Start();
        var result = (InspectionRegion[]?)
            edit.Invoke(
                null,
                new object?[]
                {
                    new[]
                    {
                        new InspectionRegion(
                            "参数说明验证",
                            ERegionKind.Text,
                            new PixelRect(1, 2, 30, 40),
                            true
                        ),
                    },
                    null,
                }
            );
        timer.Stop();
        if (failure != null)
        {
            throw new InvalidOperationException("Native Chinese ROI parameter/help probe failed.", failure);
        }

        if (
            !checkedHelp
            || result == null
            || result.Single().Field.GlyphTolerance != 0
            || result.Single().Kind != ERegionKind.Text
            || result.Single().Field.MaximumDifference != .18
        )
        {
            throw new InvalidOperationException(
                "Chinese parameter editing failed to preserve configuration semantics."
            );
        }
    }

    internal static void VerifyFindingFilter(LabelInspectionControl control, bool single = false)
    {
        var report = control.LastReport!;
        var evidence = Descendants(control).OfType<ListView>().First();
        var item = evidence
            .Items.Cast<ListViewItem>()
            .First(i =>
                i.Tag is Tuple<string, InspectionFinding> t
                && report.Analysis.Regions.Any(r => r.RegionName == t.Item1 && r.Glyphs.Count > 0)
                && (
                    !single
                    || report.EvidenceGroups.Any(g =>
                        ReferenceEquals(g.Summary, t.Item2)
                        && g.Children.Any(c => c.Finding.Code == "missing_template")
                    )
                )
            );
        var tag = (Tuple<string, InspectionFinding>)item.Tag!;
        item.Selected = true;
        var cards = Descendants(control)
            .OfType<FlowLayoutPanel>()
            .Where(c => c.Tag is Tuple<string, GlyphInspection>)
            .ToArray();
        int expected = report.Analysis.Regions.Single(r => r.RegionName == tag.Item1).Glyphs.Count;
        if (
            single
            && !Descendants(control)
                .OfType<ListView>()
                .Any(v =>
                    v.Name == "RoiEvidenceDetails"
                    && v.Items.Cast<ListViewItem>()
                        .Any(i =>
                            i.Tag is InspectionEvidenceDetail child
                            && child.Finding.Code == "missing_template"
                        )
                )
        )
        {
            throw new InvalidOperationException("Missing-reference child disappeared from ROI detail view.");
        }

        if (cards.Count(c => c.Visible) != expected)
        {
            throw new InvalidOperationException("F selection did not isolate corresponding glyphs.");
        }

        if (cards.Where(c => c.Visible).Any(c => ((Tuple<string, GlyphInspection>)c.Tag!).Item1 != tag.Item1))
        {
            throw new InvalidOperationException("Unrelated region leaked into F selection.");
        }

        Descendants(control).OfType<Button>().Single(b => b.Text == "显示全部单字").PerformClick();
        if (cards.Count(c => c.Visible) != report.Analysis.Regions.Sum(r => r.Glyphs.Count))
        {
            throw new InvalidOperationException("Show-all did not restore gallery.");
        }

        var viewer = FindViewer(control)!;
        var frame = control.LastRequest!.Actual;
        var bounds = tag.Item2.Bounds!.Value;
        float scale = Math.Min(
            Math.Max(1, viewer.ClientSize.Width - 24) / (float)frame.Width,
            Math.Max(1, viewer.ClientSize.Height - 24) / (float)frame.Height
        );
        var point = new Point(
            (int)
                Math.Round(
                    (viewer.ClientSize.Width - frame.Width * scale) / 2
                        + (bounds.X + bounds.Width / 2.0) * scale
                ),
            (int)
                Math.Round(
                    (viewer.ClientSize.Height - frame.Height * scale) / 2
                        + (bounds.Y + bounds.Height / 2.0) * scale
                )
        );
        SendMessage(viewer.Handle, 0x0201, new IntPtr(1), Pack(point));
        SendMessage(viewer.Handle, 0x0202, IntPtr.Zero, Pack(point));
        if (cards.Count(c => c.Visible) != expected)
        {
            throw new InvalidOperationException("Image F overlay did not select corresponding glyphs.");
        }

        Descendants(control).OfType<Button>().Single(b => b.Text == "显示全部单字").PerformClick();
    }

    internal static void VerifyMissingOcrMessage(LabelInspectionControl control)
    {
        if (
            !Descendants(control)
                .OfType<Label>()
                .Any(l => l.Name == "GlyphEmptyReason" && l.Text.Contains("OCR"))
        )
        {
            throw new InvalidOperationException("Missing OCR leaves the glyph gallery silently empty.");
        }
    }

    internal static void ShowGlyphTab(LabelInspectionControl control)
    {
        foreach (var tab in Descendants(control).OfType<TabControl>())
        {
            if (tab.TabPages.Count > 1)
            {
                tab.SelectedIndex = 1;
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static IntPtr Pack(Point point)
    {
        return new IntPtr((point.Y << 16) | (point.X & 0xffff));
    }

    private static ImageViewerControl? FindViewer(Control root)
    {
        if (root is ImageViewerControl viewer)
        {
            return viewer;
        }

        foreach (Control child in root.Controls)
        {
            var found = FindViewer(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
