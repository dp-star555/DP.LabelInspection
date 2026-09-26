using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using DP.Vision.Algorithms;

namespace DP.LabelInspection.Demo.WinForms;

/// <summary>
/// 局部块异常检测演示：<c>--anomaly-demo 配方.json 良品目录 待检目录 输出目录 [骨干网络.onnx]</c>。
/// 按配方每个ROI用良品目录中的整图训练一个模型，再逐张检测待检图，输出叠加图（橙色热力、红框异常）、
/// summary.csv（每图每ROI一行）和各ROI模型文件。图像须已与配方对齐（固定相机）。
/// </summary>
internal static class AnomalyDemo
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    internal static int Run(
        string recipePath,
        string goodDirectory,
        string testDirectory,
        string outputDirectory,
        string? backbonePath = null
    )
    {
        var codec = new OpenCvImageCodec();
        var store = new InspectionStore(Path.Combine(Path.GetTempPath(), "dp-anomaly-demo"), codec);
        var recipe = store.DeserializeRecipe(File.ReadAllText(recipePath));
        var good = Images(goodDirectory).Select(p => codec.Decode(File.ReadAllBytes(p))).ToList();
        if (good.Count == 0)
        {
            Console.Error.WriteLine("良品目录没有图像。");
            return 2;
        }

        Directory.CreateDirectory(Path.Combine(outputDirectory, "models"));
        // 提供ONNX骨干网络时用CNN特征（OpenCV DNN运行），否则用手工特征。
        using var cnn =
            backbonePath == null ? null : new DP.Vision.OpenCv.OpenCvCnnPatchAnomalyDetector(backbonePath);
        var detector = cnn == null ? new RegionAnomalyDetector() : new RegionAnomalyDetector(cnn);
        Console.WriteLine(cnn == null ? "特征：手工块特征" : $"特征：CNN骨干网络 {cnn.FeatureSource}");
        var options = recipe
            .Regions.Where(r => r.Kind != ERegionKind.Ignore)
            .ToDictionary(r => r.Name, RegionAnomalyDetector.DefaultOptions);
        var regions = recipe.Regions.Where(r => r.Kind != ERegionKind.Ignore).ToList();
        var models = new Dictionary<string, PatchAnomalyModel>();
        foreach (var region in regions)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var model = detector.Train(good, region, options[region.Name]);
            models[region.Name] = model;
            File.WriteAllBytes(
                Path.Combine(outputDirectory, "models", Safe(region.Name) + ".dppa"),
                model.ToBytes()
            );
            Console.WriteLine(
                $"训练 {region.Name}（{(model.Radius > 0 ? "位置相关" : "与位置无关")}）: {good.Count}张良品，记忆库{model.Count}块，阈值{model.Threshold:F3}，{watch.ElapsedMilliseconds}ms；{model.Calibration}"
            );
        }

        var summary = new StringBuilder("image,region,verdict,anomalies,max_score,threshold,ratio\n");
        foreach (string path in Images(testDirectory))
        {
            var image = codec.Decode(File.ReadAllBytes(path));
            var results = regions
                .Select(r => detector.Inspect(image, r, models[r.Name], options[r.Name]))
                .ToList();
            foreach (var r in results)
            {
                summary.AppendLine(
                    string.Join(
                        ",",
                        Path.GetFileName(path),
                        r.RegionName,
                        r.Passed ? "OK" : "NG",
                        r.Findings.Count(f => f.Verdict == EInspectionVerdict.Ng)
                            .ToString(CultureInfo.InvariantCulture),
                        r.MaximumScore.ToString("F4", CultureInfo.InvariantCulture),
                        r.Threshold.ToString("F4", CultureInfo.InvariantCulture),
                        r.Ratio.ToString("F2", CultureInfo.InvariantCulture)
                    )
                );
            }

            File.WriteAllBytes(
                Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + "-anomaly.png"),
                codec.EncodePng(Overlay(image, results))
            );
            int ng = results.Count(r => !r.Passed);
            Console.WriteLine(
                $"{Path.GetFileName(path)}: {(ng == 0 ? "OK" : "NG")}，{ng}/{results.Count}个ROI有异常；最大 {results.Max(r => r.Ratio):F2}倍阈值"
            );
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "summary.csv"),
            summary.ToString(),
            new UTF8Encoding(true)
        );
        return 0;
    }

    private static IEnumerable<string> Images(string directory)
    {
        return Directory
            .GetFiles(directory)
            .Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static string Safe(string name)
    {
        return new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>整图转为BGR，热力≥64处按强度叠加橙/红色，异常区域画红框，ROI画蓝框。</summary>
    private static ImageFrame Overlay(ImageFrame image, IReadOnlyList<RegionAnomalyResult> results)
    {
        byte[] source = image.CopyPixels();
        bool gray = image.Format == EImagePixelFormat.Gray8;
        var bgr = new byte[image.Width * image.Height * 3];
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                bgr[i * 3 + c] = gray ? source[i] : source[i * 3 + c];
            }
        }

        void Set(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                return;
            }

            int i = (y * image.Width + x) * 3;
            bgr[i] = b;
            bgr[i + 1] = g;
            bgr[i + 2] = r;
        }

        void Box(PixelRect b, byte blue, byte green, byte red)
        {
            for (int t = 0; t < 2; t++)
            {
                for (int x = b.X - t; x < b.X + b.Width + t; x++)
                {
                    Set(x, b.Y - t, blue, green, red);
                    Set(x, b.Y + b.Height - 1 + t, blue, green, red);
                }

                for (int y = b.Y - t; y < b.Y + b.Height + t; y++)
                {
                    Set(b.X - t, y, blue, green, red);
                    Set(b.X + b.Width - 1 + t, y, blue, green, red);
                }
            }
        }

        foreach (var r in results)
        {
            if (r.HeatMap != null)
            {
                byte[] heat = r.HeatMap.CopyPixels();
                for (int y = 0; y < r.Crop.Height; y++)
                {
                    for (int x = 0; x < r.Crop.Width; x++)
                    {
                        int h = heat[y * r.Crop.Width + x];
                        if (h < 64)
                        {
                            continue;
                        }

                        double a = Math.Min(1, (h - 64) / 128.0) * .7;
                        int i = ((r.Crop.Y + y) * image.Width + r.Crop.X + x) * 3;
                        bgr[i] = (byte)(bgr[i] * (1 - a));
                        bgr[i + 1] = (byte)(bgr[i + 1] * (1 - a) + (h >= 128 ? 0 : 160 * a));
                        bgr[i + 2] = (byte)(bgr[i + 2] * (1 - a) + 255 * a);
                    }
                }
            }

            Box(r.Crop, 255, 160, 0);
            foreach (var f in r.Findings.Where(f => f.Verdict == EInspectionVerdict.Ng && f.Bounds.HasValue))
            {
                var b = f.Bounds!.Value;
                Box(new PixelRect(b.X - 2, b.Y - 2, b.Width + 4, b.Height + 4), 0, 0, 255);
            }
        }

        return new ImageFrame(image.Width, image.Height, EImagePixelFormat.Bgr24, bgr);
    }
}
