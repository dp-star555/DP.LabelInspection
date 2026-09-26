using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>
/// 导出对比评估数据集并运行方法B：各图配准、按行分割，按方法B训练时的方式归一化字符（同键同尺寸），
/// 写出<c>index.csv</c>与字符图（<c>cells/</c>），方法B的得分写到<c>results/dp-b.csv</c>：
/// 测试字符的得分（另附产品当前的阈值供参考），以及训练字符按整张图留一的得分（对比脚本据此统一标定阈值）。
/// 评估轮次：all（全部训练良品训练，测试good与defect图）；留一法时另有loo-图名（该训练良品不参与训练，作为良品测试）。
/// </summary>
internal sealed class BenchmarkExporter
{
    private readonly BenchmarkConfig _config;
    private readonly string _imageRoot;
    private readonly OpenCvImageCodec _codec = new OpenCvImageCodec();

    internal BenchmarkExporter(BenchmarkConfig config, string configDirectory)
    {
        _config = config;
        _imageRoot = Path.GetFullPath(Path.Combine(configDirectory, config.ImageRoot ?? "."));
        if (
            config.Lines.Count == 0
            || config.Lines.Any(l => l.Box.Length != 4 || string.IsNullOrWhiteSpace(l.Name))
        )
        {
            throw new ArgumentException("每行需要name与box[x,y,w,h]。");
        }

        if (config.Lines.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != config.Lines.Count)
        {
            throw new ArgumentException("行名称必须唯一。");
        }

        var roles = new[] { "train", "good", "defect" };
        if (config.Images.Any(i => Array.IndexOf(roles, i.Role) < 0))
        {
            throw new ArgumentException("role只能是train、good或defect。");
        }

        if (config.Images.Count(i => i.Role == "train") < 2)
        {
            throw new ArgumentException("至少需要2张train良品（留一法标定阈值）。");
        }
    }

    internal void Run(string output)
    {
        var watch = Stopwatch.StartNew();
        var images = Load();
        Console.WriteLine($"已载入{images.Count}张图（{watch.ElapsedMilliseconds} ms）。");
        var train = images.Where(i => i.Source.Role == "train").ToList();
        var runs = new List<(
            string name,
            List<LoadedImage> train,
            List<(LoadedImage image, bool heldOut)> test
        )>
        {
            ("all", train, images.Where(i => i.Source.Role != "train").Select(i => (i, false)).ToList()),
        };
        if (_config.LeaveOneOut && train.Count >= 3)
        {
            foreach (var held in train)
            {
                runs.Add(
                    (
                        "loo-" + held.Stem,
                        train.Where(t => t != held).ToList(),
                        new List<(LoadedImage, bool)> { (held, true) }
                    )
                );
            }
        }

        var algorithm = new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector();
        var detector = new CharacterAnomalyDetector(algorithm)
        {
            LocalRadius = _config.LocalRadius,
            ThresholdMargin = _config.ThresholdMargin,
            MaximumSamples = _config.MaximumSamples,
        };
        using var index = new Csv(
            Path.Combine(output, "index.csv"),
            "run",
            "id",
            "key",
            "group",
            "char",
            "split",
            "truth",
            "image",
            "line",
            "position",
            "width",
            "height",
            "path"
        );
        using var ours = new Csv(
            Path.Combine(output, "results", "dp-b.csv"),
            "run",
            "id",
            "score",
            "model_threshold",
            "product_threshold"
        );
        foreach (var (name, runTrain, runTest) in runs)
        {
            watch.Restart();
            var trainSamples = runTrain.SelectMany(i => i.Lines.Select(l => (image: i, line: l))).ToList();
            var testSamples = runTest
                .SelectMany(t => t.image.Lines.Select(l => (t.image, line: l, t.heldOut)))
                .ToList();
            var cells = detector.NormalizeCells(
                trainSamples.Select(s => Sample(s.image, s.line)).ToArray(),
                testSamples.Select(s => Sample(s.image, s.line)).ToArray()
            );
            foreach (var cell in cells)
            {
                bool training = cell.Training;
                var (image, line) = training
                    ? trainSamples[cell.Sample]
                    : (
                        testSamples[cell.Sample - trainSamples.Count].image,
                        testSamples[cell.Sample - trainSamples.Count].line
                    );
                var patch = line.Segmentation.Characters[cell.Index];
                int position = patch.TokenIndex + 1;
                string id = Id(image, line, position);
                string truth = training
                    ? "train"
                    : image.Truth(
                        line.Line.Name,
                        position,
                        testSamples[cell.Sample - trainSamples.Count].heldOut
                    );
                string path = string.Join(
                    "/",
                    "cells",
                    name,
                    SafeKey(line.Group, patch.Character),
                    training ? "train" : "test",
                    SafeFile(id) + ".png"
                );
                string full = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, _codec.EncodePng(cell.Image));
                index.Row(
                    new object[]
                    {
                        name,
                        id,
                        cell.Key,
                        line.Group,
                        patch.Character,
                        training ? "train" : "test",
                        truth,
                        image.Source.File,
                        line.Line.Name,
                        position,
                        cell.Image.Width,
                        cell.Image.Height,
                        path,
                    }
                );
            }

            long exported = watch.ElapsedMilliseconds;
            watch.Restart();
            var models = Train(detector, algorithm, runTrain);
            int scored = 0;
            foreach (var (image, line, _) in testSamples)
            {
                foreach (var (id, score, model, threshold) in Score(detector, models, image, line))
                {
                    ours.Row(new object[] { name, id, score, model, threshold });
                    scored++;
                }
            }

            long tested = watch.ElapsedMilliseconds;
            watch.Restart();
            // 按整张图留一：该图全部字符不参与训练再评分，同一行中重复的字符（如两个“0”）不会互相“作证”。
            int calibrated = 0;
            foreach (var held in runTrain)
            {
                var others = Train(detector, algorithm, runTrain.Where(t => t != held));
                foreach (var line in held.Lines)
                {
                    foreach (var (id, score, _, _) in Score(detector, others, held, line))
                    {
                        ours.Row(new object[] { name, id, score, "", "" });
                        calibrated++;
                    }
                }
            }

            Console.WriteLine(
                $"{name}：训练{runTrain.Count}张/{trainSamples.Count}行、测试{testSamples.Count}行，导出{cells.Count}字（{exported} ms）；"
                    + $"方法B训练并检测{scored}字{tested} ms，按图留一评分{calibrated}字{watch.ElapsedMilliseconds} ms。"
            );
        }
    }

    private static Dictionary<string, CharacterAnomalyModel> Train(
        CharacterAnomalyDetector detector,
        IPatchAnomalyDetector algorithm,
        IEnumerable<LoadedImage> images
    )
    {
        var samples = images.SelectMany(i => i.Lines.Select(l => Sample(i, l))).ToArray();
        return detector
            .Train(samples)
            .ToDictionary(
                e => e.Key,
                e => new CharacterAnomalyModel(e, PatchAnomalyModel.FromBytes(e.CopyModel()), algorithm),
                StringComparer.Ordinal
            );
    }

    /// <summary>一行中已检测字符的得分；返回（编号、最大得分、模型自身阈值、产品阈值（含中位数下限））。</summary>
    private static IEnumerable<(string id, double score, double model, double threshold)> Score(
        CharacterAnomalyDetector detector,
        Dictionary<string, CharacterAnomalyModel> models,
        LoadedImage image,
        LoadedLine line
    )
    {
        var result = detector.Inspect(
            image.Frame,
            line.Segmentation.Characters,
            line.Roi,
            c => models.TryGetValue(AnomalyModelEntry.CharacterKey(line.Group, c), out var m) ? m : null
        );
        return result
            .Scores.Where(s => s.Status == "compared")
            .Select(s =>
                (
                    Id(image, line, s.TokenIndex + 1),
                    s.MaximumScore,
                    models[AnomalyModelEntry.CharacterKey(line.Group, s.Character)].Model.Threshold,
                    s.Threshold
                )
            )
            .ToList();
    }

    private static CharacterAnomalySample Sample(LoadedImage image, LoadedLine line)
    {
        return new CharacterAnomalySample(image.Frame, line.Segmentation.Characters, group: line.Group);
    }

    private static string Id(LoadedImage image, LoadedLine line, int position)
    {
        return image.Stem + "#" + line.Line.Name + "#" + position;
    }

    private List<LoadedImage> Load()
    {
        using var aligner = _config.Reference == null ? null : new LabelAligner(ReadGray(_config.Reference));
        var segmenter = new CharacterSegmenter();
        var images = new List<LoadedImage>();
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _config.Images)
        {
            string stem = Path.GetFileNameWithoutExtension(source.File);
            if (!stems.Add(stem))
            {
                throw new ArgumentException($"图像文件名（不含扩展名）必须唯一：{stem}");
            }

            using var raw = ReadGray(source.File);
            using var gray =
                aligner == null
                || string.Equals(source.File, _config.Reference, StringComparison.OrdinalIgnoreCase)
                    ? raw.Clone()
                    : aligner.Align(raw);
            var frame = Frame(gray);
            var lines = new List<LoadedLine>();
            foreach (var line in _config.Lines)
            {
                string text = line.Text;
                if (source.Texts != null && source.Texts.TryGetValue(line.Name, out var own))
                {
                    text = own;
                }

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var box = new Rect(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
                var shift = aligner?.LineShift(gray, box) ?? new Point(0, 0);
                var roi = new PixelRect(box.X + shift.X, box.Y + shift.Y, box.Width, box.Height);
                var segmentation = segmenter.Segment(frame, roi, text);
                if (segmentation.Status != "provisional")
                {
                    Console.WriteLine(
                        $"跳过 {source.File} 行[{line.Name}]：分割{segmentation.Status}（{segmentation.Reason}）"
                    );
                    continue;
                }

                lines.Add(new LoadedLine(line, roi, segmentation));
            }

            images.Add(new LoadedImage(source, stem, frame, lines));
        }

        return images;
    }

    private Mat ReadGray(string file)
    {
        // 用字节解码：Windows上Cv2.ImRead不支持中文路径。
        var bytes = File.ReadAllBytes(Path.Combine(_imageRoot, file));
        var mat = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        if (mat.Empty())
        {
            throw new IOException("无法解码图像：" + file);
        }

        return mat;
    }

    private static ImageFrame Frame(Mat gray)
    {
        using var c = gray.Clone();
        var bytes = new byte[c.Rows * c.Cols];
        Marshal.Copy(c.Data, bytes, 0, bytes.Length);
        return new ImageFrame(c.Cols, c.Rows, EImagePixelFormat.Gray8, bytes);
    }

    /// <summary>目录名：组名去掉非法字符，字符用Unicode编码（Windows文件名不区分大小写，a与A须分开）。</summary>
    internal static string SafeKey(string group, string character)
    {
        return SafeFile(group) + "~" + string.Concat(character.Select(c => "U" + ((int)c).ToString("X4")));
    }

    private static string SafeFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '#', '/', '\\', ':', '*', '?', '"', '<', '>', '|' });
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
