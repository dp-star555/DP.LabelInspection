using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Runtime.Recognition;
using DP.LabelInspection.Storage;

namespace DP.LabelInspection.Demo.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool smoke = args.Length == 2 && args[0] == "--smoke";
        var codec = new OpenCvImageCodec();
        var store = new InspectionStore(
            Environment.GetEnvironmentVariable("DP_LABEL_DATA")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DP.LabelInspection"
                ),
            codec
        );
        string? model = Environment.GetEnvironmentVariable("DP_LABEL_REC_MODEL");
        using var recognizer = string.IsNullOrWhiteSpace(model)
            ? null
            : new OnnxTextLineRecognizer(model!, new OpenCvTextLinePreprocessor());
        using var backend = new OpenCvInspectionBackend(
            recognizer,
            libraries: store,
            barcode: new ZxingBarcodeDecoder()
        );
        using var engine = new InspectionEngine(backend);
        var app = new Application();
        var window = new Window
        {
            Title = "DP.LabelInspection · Native WPF",
            Width = 1080,
            Height = 800,
        };
        var control = new DP.LabelInspection.Wpf.LabelInspectionControl();
        control.AttachEngine(engine);
        var root = new DockPanel();
        var menu = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        root.Children.Add(control);
        window.Content = root;
        var load = new Button { Content = "载入待检图", Margin = new Thickness(6) };
        load.Click += (_, _) =>
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg",
                };
                if (dialog.ShowDialog() == true)
                {
                    using var source = AlgorithmContractAdapter.ToVision(
                        codec.Decode(File.ReadAllBytes(dialog.FileName))
                    );
                    control.SetActualImage(source);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        };
        menu.Children.Add(load);
        var reference = new Button { Content = "载入参考图", Margin = new Thickness(6) };
        reference.Click += (_, _) =>
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg",
                };
                if (dialog.ShowDialog() == true)
                {
                    using var source = AlgorithmContractAdapter.ToVision(
                        codec.Decode(File.ReadAllBytes(dialog.FileName))
                    );
                    control.SetReferenceImage(source);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        };
        menu.Children.Add(reference);
        var pixels = Enumerable.Repeat((byte)255, 120 * 80).ToArray();
        for (int y = 20; y < 40; y++)
        {
            for (int x = 30; x < 50; x++)
            {
                pixels[y * 120 + x] = 0;
            }
        }

        using (
            var source = DP.Vision.VisionImage.CopyFrom(
                new DP.Vision.ImageInfo(120, 80, DP.Vision.EPixelLayout.Gray8),
                pixels
            )
        )
            control.SetActualImage(source);
        control.SetRegions(
            new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(10, 10, 90, 60)) }
        );
        window.Closed += (_, _) => control.Dispose();
        bool close = false;
        window.Closing += async (_, e) =>
        {
            if (close)
            {
                return;
            }

            e.Cancel = true;
            try
            {
                await control.CancelAndWaitAsync();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }

            close = true;
            await window.Dispatcher.InvokeAsync(new Action(window.Close));
        };
        if (smoke)
        {
            window.Loaded += async (_, _) =>
            {
                try
                {
                    control.ConfigureRoiTasks("blank", false, false, null);
                    if (control.Regions[0].Tasks.CheckQuality)
                    {
                        throw new InvalidOperationException("WPF ROI project edit was not applied.");
                    }

                    control.ConfigureRoiTasks("blank", false, true, null);
                    if (control.Regions[0].Tasks.ReadData || !control.Regions[0].Tasks.CheckQuality)
                    {
                        throw new InvalidOperationException("WPF ROI project edit lost independent flags.");
                    }

                    await Task.Delay(150);
                    var report = await control.RunInspectionAsync();
                    if (report.Verdict != EInspectionVerdict.Ng)
                    {
                        throw new InvalidOperationException("WPF native inspection failed.");
                    }

                    await Task.Delay(100);
                    var bitmap = new RenderTargetBitmap(
                        (int)window.ActualWidth,
                        (int)window.ActualHeight,
                        96,
                        96,
                        PixelFormats.Pbgra32
                    );
                    bitmap.Render(window);
                    if (
                        !control.RenderingBackend.StartsWith("DP.Vision.WPF", StringComparison.Ordinal)
                        || control.CachedDisplayPixelBytes <= 0
                        || control.DisplayPixelLayout != "Gray8"
                    )
                    {
                        throw new InvalidOperationException(
                            "WPF workbench did not use the native Gray8 tile renderer."
                        );
                    }

                    string file = Path.GetFullPath(args[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(file))
                    {
                        encoder.Save(output);
                    }

                    File.WriteAllText(
                        file + ".txt",
                        "x64="
                            + Environment.Is64BitProcess
                            + "; verdict="
                            + report.Verdict
                            + "; native_wpf=True; roi_projects=True"
                    );
                }
                catch (Exception e)
                {
                    Environment.ExitCode = 1;
                    File.WriteAllText(args[1] + ".error.txt", e.ToString());
                }
                finally
                {
                    window.Close();
                }
            };
        }

        app.Run(window);
    }
}
