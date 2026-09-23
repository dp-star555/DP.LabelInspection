using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Runtime.Detection;
using DP.LabelInspection.Runtime.Recognition;
using DP.LabelInspection.Storage;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Demo.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--geometry-probe")
        {
            try
            {
                UiInteractionProbe.VerifyGeometryFile(args[1]);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }

            return;
        }

        bool smoke = args.Length == 2 && (args[0] == "--smoke" || args[0] == "--smoke-auto");
        string? assets = FindAssets();
        var codec = new OpenCvImageCodec();
        string root =
            Environment.GetEnvironmentVariable("DP_LABEL_DATA")
            ?? (
                smoke
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "desktop-smoke-data")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DP.LabelInspection"
                    )
            );
        var store = new InspectionStore(root, codec);
        if (assets != null)
        {
            foreach (string id in new[] { "production-regular", "production-narrow" })
            {
                store.InstallSeed(File.ReadAllText(Path.Combine(assets, "libraries", id + ".json")));
            }
        }

        using var form = new Form
        {
            Text = "DP.LabelInspection · 标签检测工作台 · 实验阈值/非工业认证",
            Width = 1280,
            Height = 900,
            StartPosition = FormStartPosition.CenterScreen,
        };
        var control = new LabelInspectionControl { Dock = DockStyle.Fill };
        control.AttachLibraryManager(store);
        InspectionEngine engine = new InspectionEngine(
            new OpenCvInspectionBackend(libraries: store, barcode: new ZxingBarcodeDecoder()),
            true
        );
        control.AttachEngine(engine);
        SetSample(control);
        var modelStatus = new Label
        {
            AutoSize = true,
            Padding = new Padding(5),
            ForeColor = Color.DarkRed,
            Text = "OCR未加载：单字分割/参考比较不可用",
        };
        void LoadModel(string path)
        {
            _ = control.CreateRequest();
            var recognizer = new OnnxTextLineRecognizer(path, new OpenCvTextLinePreprocessor());
            ITextRegionDetector? detector = null;
            try
            {
                string det =
                    Environment.GetEnvironmentVariable("DP_LABEL_DET_MODEL")
                    ?? Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(path))!,
                        "ch_PP-OCRv4_det_infer.onnx"
                    );
                if (File.Exists(det))
                {
                    detector = new TextRegionDetector(
                        new DP.Vision.OnnxDetection.OnnxTextRegionDetector(det),
                        true
                    );
                }
                else if (Environment.GetEnvironmentVariable("DP_LABEL_DET_MODEL") != null)
                {
                    throw new FileNotFoundException("配置的检测模型不存在。", det);
                }
            }
            catch
            {
                recognizer.Dispose();
                throw;
            }

            var replacement = new InspectionEngine(
                new OpenCvInspectionBackend(
                    recognizer,
                    true,
                    store,
                    new ZxingBarcodeDecoder(),
                    detector,
                    true
                ),
                true
            );
            try
            {
                control.AttachEngine(replacement);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            engine.Dispose();
            engine = replacement;
            modelStatus.Text = "OCR已加载：" + Path.GetFileName(path);
            modelStatus.ForeColor = Color.DarkGreen;
        }

        string? model = Environment.GetEnvironmentVariable("DP_LABEL_REC_MODEL");
        if (string.IsNullOrWhiteSpace(model) && (!smoke || args[0] == "--smoke-auto"))
        {
            model = FindDefaultRecognitionModel();
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                LoadModel(model!);
            }
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            engine.Dispose();
            if (smoke)
            {
                File.WriteAllText(args[1] + ".error.txt", error.ToString());
            }
            else
            {
                MessageBox.Show(error.Message, "OCR加载失败");
            }

            return;
        }

        var menu = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 85,
            Padding = new Padding(6),
        };
        var saves = new List<Task>();
        Task<string>? latestSave = null;
        bool closing = false,
            allowClose = false;
        async Task Observe(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                if (!closing && !form.IsDisposed)
                {
                    MessageBox.Show(e.Message, "报告保存失败（检测结果未更改）");
                }
            }
            finally
            {
                saves.Remove(task);
            }
        }

        control.InspectionCompleted += (_, e) =>
        {
            var request = control.LastRequest!;
            var report = e.Report;
            latestSave = Task.Run(() =>
                store.SaveReport(request, report, codec.Annotate(request.Actual, report))
            );
            saves.Add(latestSave);
            _ = Observe(latestSave);
        };
        Add(menu, "载入待检图", () => LoadImage(frame => control.SetActualImage(frame)));
        Add(menu, "载入参考图", () => LoadImage(frame => control.SetReferenceImage(frame)));
        Add(menu, "无参考模式", () => control.SetReferenceImage(null));
        Add(menu, "缺墨/污点示例", () => SetSample(control));
        Add(
            menu,
            "加载OCR模型",
            () =>
            {
                using var d = new OpenFileDialog { Filter = "可信PP-OCRv4模型|*.onnx" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    LoadModel(d.FileName);
                }
            }
        );
        Add(
            menu,
            "保存配方",
            () =>
            {
                using var d = new SaveFileDialog { Filter = "DP配方|*.json", FileName = "label.recipe.json" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(d.FileName, store.SerializeRecipe(control.CreateRequest().Recipe));
                }
            }
        );
        Add(
            menu,
            "载入配方",
            () =>
            {
                using var d = new OpenFileDialog { Filter = "DP配方|*.json" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    control.ApplyRecipe(store.DeserializeRecipe(File.ReadAllText(d.FileName)));
                }
            }
        );
        AddAsync(
            menu,
            "导出当前ZIP",
            async () =>
            {
                if (latestSave == null)
                {
                    throw new InvalidOperationException("先完成一次检测。");
                }

                string id = await latestSave;
                Export(store, id);
            }
        );
        Add(menu, "历史/人工复核", () => ShowHistory(store));
        AddAsync(
            menu,
            "逐图批量",
            async () =>
            {
                using var d = new OpenFileDialog
                {
                    Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg",
                    Multiselect = true,
                };
                if (d.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                int finished = 0;
                var errors = new List<string>();
                menu.Enabled = false;
                try
                {
                    foreach (string file in d.FileNames)
                    {
                        if (closing)
                        {
                            break;
                        }

                        try
                        {
                            using var image = Image.FromFile(file);
                            using var source = AlgorithmContractAdapter.ToVision(
                                DrawingImageConverter.FromImage(image)
                            );
                            control.SetActualImage(source, false);
                            await control.RunInspectionAsync();
                            finished++;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception error)
                        {
                            errors.Add(Path.GetFileName(file) + ": " + error.Message);
                        }
                    }
                }
                finally
                {
                    if (!form.IsDisposed)
                    {
                        menu.Enabled = true;
                    }
                }

                if (!closing)
                {
                    MessageBox.Show(
                        $"已检测{finished}张；失败{errors.Count}张。报告自动保存，可在历史中导出。\n"
                            + string.Join("\n", errors),
                        "批量完成"
                    );
                }
            }
        );
        var cases = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        cases.Items.AddRange(
            new object[]
            {
                "regular-heldout",
                "condensed-heldout",
                "random-combinations",
                "pst-source",
                "ics-source",
                "frame-1",
                "frame-2",
                "frame-3",
                "frame-4",
            }
        );
        cases.SelectedIndex = 0;
        menu.Controls.Add(cases);
        Add(
            menu,
            "加载生产样例",
            () =>
            {
                if (assets == null)
                {
                    throw new DirectoryNotFoundException(
                        "没有找到私有生产样例目录。设置DP_LABEL_SAMPLES或使用自己的图像/配方。"
                    );
                }

                LoadProduction(control, codec, assets, (string)cases.SelectedItem!);
            }
        );
        menu.Controls.Add(modelStatus);
        form.Controls.Add(control);
        form.Controls.Add(menu);
        form.FormClosing += async (_, e) =>
        {
            if (allowClose)
            {
                return;
            }

            e.Cancel = true;
            closing = true;
            try
            {
                await control.CancelAndWaitAsync();
                await Task.WhenAll(saves.ToArray());
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }

            allowClose = true;
            form.Close();
        };
        if (smoke)
        {
            form.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(150);
                    await UiInteractionProbe.VerifyBarcodeSummary(args[1] + ".barcode-comparison.png");
                    UiInteractionProbe.VerifyCanvasZoom();
                    UiInteractionProbe.VerifyImageDpiConversion();
                    UiInteractionProbe.VerifyCanvasFit(control);
                    UiInteractionProbe.VerifySmallImageFit(control);
                    UiInteractionProbe.VerifyRoiMapping(control);
                    var report = await control.RunInspectionAsync();
                    if (report.Verdict != EInspectionVerdict.Ng)
                    {
                        throw new InvalidOperationException("Synthetic defect was not NG.");
                    }

                    var text = report
                        .Analysis.Regions.Single(r => r.RegionName == "variable-text")
                        .Recognition;
                    if (string.IsNullOrWhiteSpace(model))
                    {
                        UiInteractionProbe.VerifyMissingOcrMessage(control);
                    }

                    if (!string.IsNullOrWhiteSpace(model) && text?.Text != "A1020")
                    {
                        throw new InvalidOperationException("Real OCR UI failed.");
                    }

                    string destination = Path.GetFullPath(args[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await Task.Delay(100);
                    using (var image = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(destination, ImageFormat.Png);
                    }

                    string job =
                        latestSave == null
                            ? throw new InvalidOperationException("Report not queued.")
                            : await latestSave;
                    store.AddFeedback(job, "REVIEW", "automated UI regression", "smoke");
                    string zip = destination + "." + Guid.NewGuid().ToString("N") + ".zip";
                    store.ExportReport(job, zip);
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        await UiInteractionProbe.VerifyDataBindings(control);
                        if (latestSave != null)
                        {
                            await latestSave;
                        }
                    }

                    if (assets != null && !string.IsNullOrWhiteSpace(model))
                    {
                        LoadProduction(control, codec, assets, "random-combinations");
                        var production = await control.RunInspectionAsync();
                        if (
                            production.Analysis.Regions.Sum(r =>
                                r.Glyphs.Count(g => g.Comparison?.Status == "compared")
                            ) != 36
                            || production.Analysis.Regions.Any(r =>
                                r.Glyphs.Any(g => g.Status == "exceeds_threshold")
                            )
                        )
                        {
                            throw new InvalidOperationException("Random-character UI regression failed.");
                        }

                        await Task.Delay(100);
                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(destination + ".production.png", ImageFormat.Png);
                        UiInteractionProbe.VerifyFindingFilter(control);
                        UiInteractionProbe.ShowGlyphTab(control);
                        await Task.Delay(80);
                        form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(destination + ".glyphs.png", ImageFormat.Png);
                        if (latestSave != null)
                        {
                            await latestSave;
                        }

                        await UiInteractionProbe.VerifyLibraryEditor(store, destination + ".library.png");
                        await UiInteractionProbe.VerifyQuickLibrary(
                            store,
                            engine,
                            destination + ".quick-library.png"
                        );
                        LoadProduction(control, codec, assets, "condensed-heldout");
                        await control.RunInspectionAsync();
                        UiInteractionProbe.VerifyFindingFilter(control, true);
                        if (latestSave != null)
                        {
                            await latestSave;
                        }
                    }

                    File.WriteAllText(
                        destination + ".txt",
                        $"x64={Environment.Is64BitProcess}; verdict={report.Verdict}; roi_mouse_mapping=True; roi_move_resize=True; data_binding={!string.IsNullOrWhiteSpace(model)}; evidence_filter={(assets != null && !string.IsNullOrWhiteSpace(model))}; ocr={text?.Text ?? "NOT_RUN"}; report_zip=True; production={(assets != null && !string.IsNullOrWhiteSpace(model))}"
                    );
                }
                catch (Exception error)
                {
                    Environment.ExitCode = 1;
                    File.WriteAllText(args[1] + ".error.txt", error.ToString());
                    Console.Error.WriteLine(error);
                }
                finally
                {
                    form.Close();
                }
            };
        }

        try
        {
            Application.Run(form);
        }
        finally
        {
            engine.Dispose();
        }
    }

    private static string? FindDefaultRecognitionModel()
    {
        // 仅检查已知的本地部署和工作区路径，不下载或执行Python。
        for (
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            directory != null;
            directory = directory.Parent
        )
        {
            string local = Path.Combine(directory.FullName, "models", "rec.onnx");
            if (File.Exists(local))
            {
                return local;
            }

            string workspace = Path.Combine(
                directory.FullName,
                "LabelInspection",
                ".venv",
                "Lib",
                "site-packages",
                "rapidocr_onnxruntime",
                "models",
                "ch_PP-OCRv4_rec_infer.onnx"
            );
            if (File.Exists(workspace))
            {
                return workspace;
            }
        }

        return null;
    }

    private static void Add(Control parent, string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "操作未完成");
            }
        };
        parent.Controls.Add(b);
    }

    private static void AddAsync(Control parent, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try
            {
                await action();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "操作未完成");
            }
            finally
            {
                if (!b.IsDisposed)
                {
                    b.Enabled = true;
                }
            }
        };
        parent.Controls.Add(b);
    }

    private static void LoadImage(Action<DP.Vision.IImageSource> accept)
    {
        using var d = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg" };
        if (d.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        using var image = Image.FromFile(d.FileName);
        using var source = AlgorithmContractAdapter.ToVision(DrawingImageConverter.FromImage(image));
        accept(source);
    }

    private static void Export(InspectionStore store, string id)
    {
        using var d = new SaveFileDialog { Filter = "报告ZIP|*.zip", FileName = "inspection-" + id + ".zip" };
        if (d.ShowDialog() == DialogResult.OK)
        {
            store.ExportReport(id, d.FileName);
        }
    }

    private static void ShowHistory(InspectionStore store)
    {
        using var form = new Form
        {
            Text = "历史 / 人工复核（不覆盖机器结论）",
            Width = 950,
            Height = 550,
            StartPosition = FormStartPosition.CenterParent,
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        var heads = store.History();
        foreach (var h in heads)
        {
            list.Items.Add((string?)h["utc"] + " / " + (string?)h["verdict"] + " / " + (string?)h["id"]);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 65 };
        var verdict = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        verdict.Items.AddRange(new object[] { "REVIEW", "OK", "NG" });
        verdict.SelectedIndex = 0;
        var comment = new TextBox { Width = 300 };
        buttons.Controls.AddRange(new Control[] { verdict, comment });
        string Selected()
        {
            return list.SelectedIndex < 0
                ? throw new InvalidOperationException("请选择历史记录。")
                : ((string)heads[list.SelectedIndex]["id"]!);
        }

        Add(
            buttons,
            "追加人工复核",
            () =>
            {
                store.AddFeedback(
                    Selected(),
                    (string)verdict.SelectedItem!,
                    comment.Text,
                    Environment.UserName
                );
                MessageBox.Show("已追加复核，原检测结论未更改。");
            }
        );
        Add(buttons, "导出完整ZIP", () => Export(store, Selected()));
        form.Controls.Add(list);
        form.Controls.Add(buttons);
        form.ShowDialog();
    }

    private static string? FindAssets()
    {
        string? configured = Environment.GetEnvironmentVariable("DP_LABEL_SAMPLES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 10 && directory != null; i++, directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "LabelInspection", "samples", "production");
            if (File.Exists(Path.Combine(path, "manifest.json")))
            {
                return path;
            }
        }

        return null;
    }

    private static void LoadProduction(
        LabelInspectionControl control,
        OpenCvImageCodec codec,
        string assets,
        string id
    )
    {
        var image = codec.Decode(File.ReadAllBytes(Path.Combine(assets, "frames", id + ".png")));
        var document = JObject.Parse(File.ReadAllText(Path.Combine(assets, id + ".recipe.json")));
        var regions = document["rois"]!
            .Select(r =>
            {
                var box = (JArray)r["box"]!;
                return new InspectionRegion(
                    (string)r["name"]!,
                    ERegionKind.Text,
                    new PixelRect((int)box[0], (int)box[1], (int)box[2], (int)box[3]),
                    true,
                    new FieldSettings((string?)r["glyph_library_id"], (int?)r["glyph_library_revision"])
                );
            })
            .ToArray();
        using var source = AlgorithmContractAdapter.ToVision(image);
        control.SetActualImage(source);
        control.SetReferenceImage(null, true);
        control.SetRegions(regions);
    }

    private static void SetSample(LabelInspectionControl control)
    {
        using var reference = new Bitmap(640, 250);
        using (var graphics = Graphics.FromImage(reference))
        using (var font = new Font("Arial", 27, FontStyle.Bold))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("DP LABEL", font, Brushes.Black, 30, 30);
            graphics.DrawString("A1020", font, Brushes.Black, 30, 120);
        }

        using var actual = (Bitmap)reference.Clone();
        using (var graphics = Graphics.FromImage(actual))
        {
            graphics.FillRectangle(Brushes.White, 25, 25, 50, 65);
            graphics.FillRectangle(Brushes.Black, 520, 100, 10, 10);
        }

        using var actualSource = AlgorithmContractAdapter.ToVision(DrawingImageConverter.FromImage(actual));
        using var referenceSource = AlgorithmContractAdapter.ToVision(
            DrawingImageConverter.FromImage(reference)
        );
        control.SetActualImage(actualSource);
        control.SetReferenceImage(referenceSource, true);
        control.SetRegions(
            new[]
            {
                new InspectionRegion("fixed-title", ERegionKind.Fixed, new PixelRect(20, 20, 390, 75)),
                new InspectionRegion(
                    "variable-text",
                    ERegionKind.Text,
                    new PixelRect(20, 115, 240, 65),
                    true
                ),
                new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(460, 25, 150, 185)),
            }
        );
    }
}
