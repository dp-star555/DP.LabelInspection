using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DP.LabelInspection.Runtime;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.PPOcrBaseline;

/// <summary>用真实DB模型冻结检测证据基线：候选清单，以及可离线重放的DB概率图。</summary>
/// <remarks>本工具是阶段1与阶段2的可重跑入口：同一模型与同一帧必须得到逐字节相同的候选清单。</remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3)
            {
                throw new ArgumentException(
                    "Usage: <detection.onnx> <production assets directory> <output directory>"
                        + " [--map <frame name>] [--verify <baseline candidates.json>]"
                );
            }

            Console.OutputEncoding = Encoding.UTF8;
            string modelPath = Path.GetFullPath(args[0]);
            string framesDirectory = Path.Combine(Path.GetFullPath(args[1]), "frames");
            string outputDirectory = Path.GetFullPath(args[2]);
            string? mapFrame = null;
            string? baselinePath = null;
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--map":
                        mapFrame = Next(args, ref i);
                        break;
                    case "--verify":
                        baselinePath = Path.GetFullPath(Next(args, ref i));
                        break;
                    default:
                        throw new ArgumentException("Unknown option: " + args[i]);
                }
            }

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("Detection model not found.", modelPath);
            }

            if (!Directory.Exists(framesDirectory))
            {
                throw new DirectoryNotFoundException("Frames directory not found: " + framesDirectory);
            }

            if (baselinePath != null && !File.Exists(baselinePath))
            {
                throw new FileNotFoundException("Baseline not found.", baselinePath);
            }

            Directory.CreateDirectory(outputDirectory);
            var codec = new OpenCvImageCodec();
            var rows = new List<string>();
            string modelSha256;
            using (var task = new DP.Vision.OnnxDetection.PPOcrDetectionTask(modelPath))
            {
                modelSha256 = task.ModelIdentity;
                foreach (
                    var file in Directory
                        .GetFiles(framesDirectory, "*.png")
                        .OrderBy(p => p, StringComparer.Ordinal)
                )
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    string name = Path.GetFileName(file);
                    var snapshot = codec.Decode(bytes);
                    using var source = Bridge.ToVision(snapshot);
                    var input = PPOcrDetectionPreparer.Prepare(source, default);
                    var evidence = task.Detect(input, default);
                    var candidates = DbTextRegionCandidates.Extract(evidence, default);
                    rows.Add(
                        "    {"
                            + "\"name\": \""
                            + name
                            + "\", "
                            + "\"image_sha256\": \""
                            + Sha256(bytes)
                            + "\", "
                            + "\"image_width\": "
                            + snapshot.Width.ToString(CultureInfo.InvariantCulture)
                            + ", \"image_height\": "
                            + snapshot.Height.ToString(CultureInfo.InvariantCulture)
                            + ", \"candidate_count\": "
                            + candidates.Count.ToString(CultureInfo.InvariantCulture)
                            + ", \"candidates\": ["
                            + string.Join(
                                ",",
                                candidates.Select(c =>
                                    "["
                                        + c.X.ToString(CultureInfo.InvariantCulture)
                                        + ","
                                        + c.Y.ToString(CultureInfo.InvariantCulture)
                                        + ","
                                        + c.Width.ToString(CultureInfo.InvariantCulture)
                                        + ","
                                        + c.Height.ToString(CultureInfo.InvariantCulture)
                                        + "]"
                                )
                            )
                            + "]}"
                    );
                    Console.WriteLine(
                        "BASELINE "
                            + name
                            + " candidates="
                            + candidates.Count.ToString(CultureInfo.InvariantCulture)
                    );
                    if (string.Equals(name, mapFrame, StringComparison.Ordinal))
                    {
                        WriteProbabilityMap(outputDirectory, name, bytes, evidence);
                    }
                }
            }

            string candidatesJson =
                "{\n  \"detection_model_sha256\": \""
                + modelSha256
                + "\",\n  \"frames\": [\n"
                + string.Join(",\n", rows)
                + "\n  ]\n}\n";
            string candidatesPath = Path.Combine(outputDirectory, "candidates.json");
            File.WriteAllText(candidatesPath, candidatesJson, new UTF8Encoding(false));
            Console.WriteLine("MODEL_SHA256=" + modelSha256);
            Console.WriteLine("SUMMARY frames=" + rows.Count.ToString(CultureInfo.InvariantCulture));
            if (baselinePath != null)
            {
                string baseline = File.ReadAllText(baselinePath, Encoding.UTF8);
                if (!string.Equals(baseline, candidatesJson, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("BASELINE MISMATCH: " + baselinePath);
                    return 1;
                }

                Console.WriteLine("BASELINE MATCH: " + baselinePath);
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("Missing value for " + args[index]);
        }

        return args[++index];
    }

    private static void WriteProbabilityMap(
        string outputDirectory,
        string frameName,
        byte[] imageBytes,
        DP.Vision.OnnxDetection.PPOcrDetectionOutput evidence
    )
    {
        var values = evidence.CopyValues();
        var raw = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        string fileName = "probability-" + frameName + ".f32";
        File.WriteAllBytes(Path.Combine(outputDirectory, fileName), raw);
        File.WriteAllText(
            Path.Combine(outputDirectory, "map.json"),
            "{\n  \"frame\": \""
                + frameName
                + "\",\n  \"file\": \""
                + fileName
                + "\",\n  \"detection_model_sha256\": \""
                + evidence.ModelSha256
                + "\",\n  \"image_sha256\": \""
                + Sha256(imageBytes)
                + "\",\n  \"image_width\": "
                + evidence.ImageWidth.ToString(CultureInfo.InvariantCulture)
                + ",\n  \"image_height\": "
                + evidence.ImageHeight.ToString(CultureInfo.InvariantCulture)
                + ",\n  \"map_width\": "
                + evidence.Width.ToString(CultureInfo.InvariantCulture)
                + ",\n  \"map_height\": "
                + evidence.Height.ToString(CultureInfo.InvariantCulture)
                + ",\n  \"probability_count\": "
                + values.Length.ToString(CultureInfo.InvariantCulture)
                + ",\n  \"probability_sha256\": \""
                + Sha256(raw)
                + "\"\n}\n",
            new UTF8Encoding(false)
        );
        Console.WriteLine(
            "MAP "
                + frameName
                + " "
                + evidence.Width.ToString(CultureInfo.InvariantCulture)
                + "x"
                + evidence.Height.ToString(CultureInfo.InvariantCulture)
                + " sha256="
                + Sha256(raw)
        );
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
