using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DP.LabelInspection.CompatibilityProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (!Environment.Is64BitProcess)
            {
                throw new InvalidOperationException("x64 process required.");
            }

            var bytes = Enumerable.Repeat((byte)255, 160 * 80).ToArray();
            for (int y = 20; y < 50; y++)
            {
                for (int x = 20; x < 50; x++)
                {
                    bytes[y * 160 + x] = 0;
                }
            }

            var actual = new ImageFrame(160, 80, EImagePixelFormat.Gray8, bytes);
            using var backend = new OpenCvInspectionBackend();
            using var engine = new InspectionEngine(backend);
            var recipe = new InspectionRecipe(
                "native smoke",
                160,
                80,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(10, 10, 100, 60)) }
            );
            var report = engine.Inspect(new InspectionRequest(actual, recipe));
            if (report.Verdict != EInspectionVerdict.Ng)
            {
                throw new InvalidOperationException(
                    "Native OpenCV did not detect the synthetic 900 px² spot."
                );
            }

            Console.WriteLine(
                $"OpenCV native OK; x64={Environment.Is64BitProcess}; verdict={report.Verdict}; area={report.Analysis.Regions[0].Findings[0].AreaPixels}"
            );
            if (args.Length != 1)
            {
                Console.WriteLine(
                    "OCR probe NOT RUN. Pass a PP-OCRv4 recognition ONNX model path to test actual native inference."
                );
                return 0;
            }

            using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 2 };
            using var session = new InferenceSession(args[0], options);
            string name = session.InputMetadata.Keys.Single();
            var tensor = new DenseTensor<float>(new[] { 1, 3, 48, 320 });
            using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, tensor) });
            var prediction = outputs.First().AsTensor<float>();
            if (
                prediction.Dimensions.Length != 3
                || prediction.Dimensions[0] != 1
                || prediction.Dimensions[1] < 1
                || prediction.Dimensions[2] < 100
            )
            {
                throw new InvalidOperationException("Unexpected recognition output layout.");
            }

            Console.WriteLine(
                "ONNX native inference OK; raw recognition tensor="
                    + string.Join("x", prediction.Dimensions.ToArray())
            );
            Console.WriteLine(
                "This validates runtime/model compatibility only, NOT OCR preprocessing, decoding or recognition accuracy."
            );
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
