using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Runtime.Detection;
using DP.LabelInspection.Runtime.Recognition;
using DP.LabelInspection.Storage;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace DP.LabelInspection.OcrRegression;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3)
            {
                throw new ArgumentException(
                    "Usage: <recognition.onnx> <production assets directory> <oracle.tsv>"
                );
            }

            Console.OutputEncoding = Encoding.UTF8;
            string expectedModelHash = File.ReadAllText(args[2] + ".model-sha256").Trim();
            using var recognizer = new OnnxTextLineRecognizer(
                args[0],
                new OpenCvTextLinePreprocessor(),
                expectedModelHash
            );
            string storePath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(args[2]))!,
                "regression-store-" + Guid.NewGuid().ToString("N")
            );
            var store = new InspectionStore(storePath, new OpenCvImageCodec());
            foreach (string id in new[] { "production-regular", "production-narrow" })
            {
                store.InstallSeed(File.ReadAllText(Path.Combine(args[1], "libraries", id + ".json")));
            }

            using var backend = new OpenCvInspectionBackend(recognizer, libraries: store);
            using var engine = new InspectionEngine(backend);
            int characters = 0,
                comparisons = 0,
                missing = 0,
                exceed = 0;
            int total = 0,
                textMatches = 0,
                stepMatches = 0,
                manualMatches = 0,
                real = 0;
            Console.WriteLine("MODEL_SHA256=" + recognizer.ModelSha256);
            foreach (var row in File.ReadAllLines(args[2], Encoding.UTF8))
            {
                var p = row.Split('\t');
                if (p.Length != 11)
                {
                    throw new ArgumentException("Invalid oracle row.");
                }

                string path = Path.Combine(args[1], p[2]);
                using (var sha = SHA256.Create())
                {
                    if (
                        BitConverter
                            .ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                            .Replace("-", "")
                            .ToLowerInvariant() != p[9]
                    )
                    {
                        throw new InvalidOperationException("Source hash changed.");
                    }
                }

                using var image = Cv2.ImRead(path, ImreadModes.Color);
                if (image.Empty())
                {
                    throw new InvalidOperationException("Cannot read fixture.");
                }

                var bytes = new byte[image.Rows * image.Cols * 3];
                Marshal.Copy(image.Data, bytes, 0, bytes.Length);
                var frame = new ImageFrame(image.Cols, image.Rows, EImagePixelFormat.Bgr24, bytes);
                var bounds = new PixelRect(
                    int.Parse(p[3]),
                    int.Parse(p[4]),
                    int.Parse(p[5]),
                    int.Parse(p[6])
                );
                var sourceRecipe = JObject.Parse(
                    File.ReadAllText(Path.Combine(args[1], p[0] + ".recipe.json"))
                );
                var sourceRoi = sourceRecipe["rois"]!.Single(r => (string?)r["name"] == p[1]);
                var field = new FieldSettings(
                    (string?)sourceRoi["glyph_library_id"],
                    (int?)sourceRoi["glyph_library_revision"]
                );
                var recipe = new InspectionRecipe(
                    p[0],
                    frame.Width,
                    frame.Height,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[]
                    {
                        new InspectionRegion(p[1], ERegionKind.Text, bounds, singleLine: true, field: field),
                    }
                );
                // 只有图像和几何进入SDK；基准及人工文本只在之后用于评估。
                var report = engine.Inspect(new InspectionRequest(frame, recipe));
                var regionResult = report.Analysis.Regions.Single();
                characters += regionResult.Segmentation?.Characters.Count ?? 0;
                comparisons += regionResult.Glyphs.Count(g => g.Comparison?.Status == "compared");
                missing += regionResult.Glyphs.Count(g => g.Status == "missing_template");
                exceed += regionResult.Glyphs.Count(g => g.Status == "exceeds_threshold");
                Console.WriteLine(
                    $"APPEARANCE {p[0]}/{p[1]} status={regionResult.Segmentation?.Status} chars={regionResult.Segmentation?.Characters.Count} compared={regionResult.Glyphs.Count(g => g.Comparison?.Status == "compared")} exceeds={regionResult.Glyphs.Count(g => g.Status == "exceeds_threshold")} reason={regionResult.Segmentation?.Reason}"
                );
                var result =
                    regionResult.Recognition ?? throw new InvalidOperationException("OCR evidence missing.");
                bool incomplete =
                    field.LibraryId != null
                    && (
                        regionResult.Segmentation?.Status != "provisional"
                        || regionResult.Glyphs.Count == 0
                        || regionResult.Glyphs.Any(g => g.Comparison?.Status != "compared")
                        || regionResult.Glyphs.Count != regionResult.Segmentation.Characters.Count
                    );
                if (
                    report.Verdict
                        != (
                            incomplete || regionResult.Glyphs.Any(g => g.Status == "exceeds_threshold")
                                ? EInspectionVerdict.Ng
                                : EInspectionVerdict.Ok
                        )
                    || !result.Bounds.Equals(bounds)
                )
                {
                    throw new InvalidOperationException(
                        "Requested-appearance completion verdict or coordinates changed unexpectedly."
                    );
                }

                bool textMatch = result.Text == p[7];
                bool stepMatch = string.Join(",", result.Steps.Select(s => s.ClassIndex)) == p[10];
                total++;
                if (textMatch)
                {
                    textMatches++;
                }

                if (stepMatch)
                {
                    stepMatches++;
                }

                if (result.Text == p[8])
                {
                    manualMatches++;
                }

                if (p[0] != "random-combinations")
                {
                    real++;
                }

                Console.WriteLine(
                    $"{p[0]}/{p[1]}\t{result.Text}\tpython={textMatch}\tctc={stepMatch}\tmanual={result.Text == p[8]}\tconfidence={result.Confidence:F6}\twidth={result.InputWidth}/{result.ContentWidth}\t{report.Verdict}"
                );
            }

            if (total == 0)
            {
                throw new InvalidOperationException("Empty regression set.");
            }

            // 独立执行原始窄裁剪的冻结回归。
            foreach (
                var item in new[]
                {
                    new[] { "material-line.png", "M012200015" },
                    new[] { "wafer-line.png", "Wafer" },
                }
            )
            {
                using var image = Cv2.ImRead(
                    Path.Combine(args[1], "regression", item[0]),
                    ImreadModes.Grayscale
                );
                var bytes = new byte[image.Rows * image.Cols];
                Marshal.Copy(image.Data, bytes, 0, bytes.Length);
                var frame = new ImageFrame(image.Cols, image.Rows, EImagePixelFormat.Gray8, bytes);
                var result = recognizer.Recognize(
                    frame,
                    new PixelRect(0, 0, frame.Width, frame.Height),
                    default
                );
                if (result.Text != item[1])
                {
                    throw new InvalidOperationException(
                        "Frozen regression failed: " + item[0] + " -> " + result.Text
                    );
                }

                var segmented = new CharacterSegmenter().Segment(
                    frame,
                    new PixelRect(0, 0, frame.Width, frame.Height),
                    result.Text
                );
                if (segmented.Characters.Count != item[1].Length)
                {
                    throw new InvalidOperationException("Frozen physical segmentation failed.");
                }

                if (item[0] == "material-line.png")
                {
                    var annotations = JObject.Parse(
                        File.ReadAllText(Path.Combine(args[1], "regression", "material-line.json"))
                    )["ink_x_intervals"]!;
                    using var mask = new Mat();
                    double threshold = Cv2.Threshold(
                        image,
                        mask,
                        0,
                        255,
                        ThresholdTypes.BinaryInv | ThresholdTypes.Otsu
                    );
                    int foreign = 0;
                    foreach (var character in segmented.Characters)
                    {
                        var interval = annotations[character.TokenIndex]!;
                        int left = (int)interval[0]!,
                            right = (int)interval[1]!;
                        var patch = character.Patch.CopyPixels();
                        for (int y = 0; y < character.Patch.Height; y++)
                        {
                            for (int x = 0; x < character.Patch.Width; x++)
                            {
                                int originalX = character.Bounds.X + x,
                                    originalY = character.Bounds.Y + y;
                                if (
                                    (originalX < left || originalX >= right)
                                    && mask.At<byte>(originalY, originalX) > 0
                                    && patch[y * character.Patch.Stride + x] <= threshold
                                )
                                {
                                    foreign++;
                                }
                            }
                        }
                    }

                    if (foreign != 0)
                    {
                        throw new InvalidOperationException("Known neighboring ink leaked into owned crops.");
                    }

                    Console.WriteLine("FROZEN neighbor_ink_remaining=0");
                }

                Console.WriteLine("FROZEN " + item[0] + " -> " + result.Text);
            }

            if (characters != 375 || comparisons != 370 || missing != 5 || exceed != 1)
            {
                throw new InvalidOperationException(
                    "Production appearance coverage changed; inspect evidence, do not raise thresholds."
                );
            }

            using (
                var detector = new TextRegionDetector(
                    new DP.Vision.OnnxDetection.OnnxTextRegionDetector(
                        Path.Combine(
                            Path.GetDirectoryName(Path.GetFullPath(args[0]))!,
                            "ch_PP-OCRv4_det_infer.onnx"
                        )
                    ),
                    true
                )
            )
            {
                var frame = new OpenCvImageCodec().Decode(
                    File.ReadAllBytes(Path.Combine(args[1], "frames", "regular-heldout.png"))
                );
                using var discoveryBackend = new OpenCvInspectionBackend(
                    recognizer,
                    libraries: store,
                    barcode: new ZxingBarcodeDecoder(),
                    detector: detector
                );
                using var discoveryEngine = new InspectionEngine(discoveryBackend);
                var discovery = discoveryEngine.Inspect(
                    new InspectionRequest(
                        frame,
                        new InspectionRecipe(
                            "discover",
                            frame.Width,
                            frame.Height,
                            EInspectionMode.Free,
                            EAlignmentMode.AssumeAligned,
                            Array.Empty<InspectionRegion>()
                        )
                    )
                );
                int detected = detector.Detect(frame, default).Count;
                if (
                    detected < 1
                    || discovery.Verdict != EInspectionVerdict.Ng
                    || discovery.Analysis.Regions.Count != 0
                )
                {
                    throw new InvalidOperationException(
                        "Authoring discovery failed or unconfigured inspection falsely approved."
                    );
                }

                Console.WriteLine(
                    $"DISCOVERY model={detector.ModelIdentity}; authoring_candidates={detected}; no_configured_ROI=NG; discovery is not acceptance"
                );
            }

            var tiny = new ImageFrame(1, 1, EImagePixelFormat.Gray8, new byte[] { 255 });
            try
            {
                recognizer.Recognize(
                    tiny,
                    new PixelRect(0, 0, 1, 1),
                    new System.Threading.CancellationToken(true)
                );
                throw new InvalidOperationException("Cancellation was ignored.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("CANCEL before inference: PASS");
            }

            try
            {
                using var rejected = new OnnxTextLineRecognizer(
                    args[0],
                    new OpenCvTextLinePreprocessor(),
                    new string('0', 64)
                );
                throw new InvalidOperationException("Wrong model hash accepted.");
            }
            catch (ArgumentException error) when (error.ParamName == "expectedSha256")
            {
                Console.WriteLine("MODEL hash rejection: PASS");
            }

            recognizer.Dispose();
            try
            {
                recognizer.Recognize(tiny, new PixelRect(0, 0, 1, 1), default);
                throw new InvalidOperationException("Disposed recognizer accepted work.");
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("DISPOSE lifecycle: PASS");
            }

            if (
                typeof(DP.Vision.Onnx.OnnxTextLineRecognizer)
                    .Assembly.GetReferencedAssemblies()
                    .Any(a =>
                        a.Name!.Contains("OpenCv")
                        || a.Name.Contains("Halcon")
                        || a.Name.Contains("Windows.Forms")
                    )
            )
            {
                throw new InvalidOperationException("OCR adapter depends on concrete vision/UI types.");
            }

            Console.WriteLine(
                $"APPEARANCE SUMMARY characters={characters}; comparisons={comparisons}; missing={missing}; exceed={exceed}"
            );
            Directory.Delete(storePath, true);
            Console.WriteLine(
                $"SUMMARY rows={total}; real={real}; synthetic={total - real}; python_text={textMatches}; ctc={stepMatches}; manual_exact={manualMatches}; scoped_roi_verdicts=True; x64={Environment.Is64BitProcess}"
            );
            return total == textMatches && total == stepMatches && Environment.Is64BitProcess ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
