using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using OpenCvSharp;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>
/// 生成合成标签与配置，用于在没有实拍图时检查整条对比流程：三行不同字体/字号的文字，
/// 10张图（6张训练良品、2张测误报良品、2张缺陷：斑驳的两个“0”、断开的“6”）。合成图不能代替实拍评估。
/// </summary>
internal static class SyntheticLabels
{
    private const int Scale = 4;

    private static readonly (
        string Name,
        string Text,
        HersheyFonts Font,
        double Size,
        int Thickness,
        Point Origin
    )[] Lines =
    {
        ("MS", "3P1100B", HersheyFonts.HersheySimplex, 1.2, 3, new Point(30, 60)),
        ("LOT", "12605350C7A01", HersheyFonts.HersheyDuplex, 0.9, 2, new Point(30, 130)),
        ("WF", "WF675907", HersheyFonts.HersheyComplex, 1.0, 2, new Point(30, 200)),
    };

    internal static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var images = new List<BenchmarkImage>();
        for (int i = 0; i < 10; i++)
        {
            string role =
                i < 6 ? "train"
                : i < 8 ? "good"
                : "defect";
            string file = $"label{i + 1:00}.png";
            var defects = new Dictionary<string, int[]>();
            var random = new Random(100 + i);
            // 4倍分辨率绘制后缩小：亚像素位置、墨色、笔画粗细与模糊每张不同，接近实拍良品之间的差异。
            using var big = new Mat(240 * Scale, 460 * Scale, MatType.CV_8UC1, Scalar.All(232));
            int ink = random.Next(20, 50);
            foreach (var line in Lines)
            {
                var origin = new Point(
                    line.Origin.X * Scale + random.Next(-Scale, Scale + 1),
                    line.Origin.Y * Scale + random.Next(-Scale, Scale + 1)
                );
                int thickness = line.Thickness * Scale + random.Next(-1, 2);
                Cv2.PutText(
                    big,
                    line.Text,
                    origin,
                    line.Font,
                    line.Size * Scale,
                    Scalar.All(ink),
                    thickness,
                    LineTypes.AntiAlias
                );
                if (i == 8 && line.Name == "MS")
                {
                    // 斑驳：第5、6位的“0”笔画内出现小白点。
                    Mottle(big, line, origin, 4, random);
                    Mottle(big, line, origin, 5, random);
                    defects["MS"] = new[] { 5, 6 };
                }

                if (i == 9 && line.Name == "WF")
                {
                    // 断笔：第3位的“6”中部横向抹去一条。
                    var (left, width) = Span(line, origin, 2);
                    Cv2.Rectangle(
                        big,
                        new Rect(left, origin.Y - 13 * Scale, width, 4 * Scale),
                        Scalar.All(232),
                        -1
                    );
                    defects["WF"] = new[] { 3 };
                }
            }

            using var m = new Mat();
            Cv2.Resize(big, m, new Size(460, 240), 0, 0, InterpolationFlags.Area);
            using var noise = new Mat(m.Size(), MatType.CV_8UC1);
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(4));
            using var noisy = new Mat();
            Cv2.Add(m, noise, noisy);
            Cv2.GaussianBlur(noisy, noisy, new Size(5, 5), 0.4 + random.NextDouble() * 0.5);
            Cv2.ImWrite(Path.Combine(directory, file), noisy);
            images.Add(
                new BenchmarkImage
                {
                    File = file,
                    Role = role,
                    Defects = defects.Count > 0 ? defects : null,
                }
            );
        }

        var config = new BenchmarkConfig { Images = images };
        foreach (var line in Lines)
        {
            var size = Cv2.GetTextSize(line.Text, line.Font, line.Size, line.Thickness, out int baseline);
            config.Lines.Add(
                new BenchmarkLine
                {
                    Name = line.Name,
                    Text = line.Text,
                    Box = new[]
                    {
                        line.Origin.X - 12,
                        line.Origin.Y - size.Height - 12,
                        size.Width + 24,
                        size.Height + baseline + 20,
                    },
                }
            );
        }

        File.WriteAllText(
            Path.Combine(directory, "bench.json"),
            JsonConvert.SerializeObject(
                config,
                Formatting.Indented,
                new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    // 只把属性名改为camelCase，字典键（行名称）保持原样。
                    ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                    {
                        NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy(),
                    },
                }
            )
        );
    }

    /// <summary>4倍分辨率下第<paramref name = "index"/>个字符的水平范围。</summary>
    private static (int Left, int Width) Span(
        (string Name, string Text, HersheyFonts Font, double Size, int Thickness, Point Origin) line,
        Point origin,
        int index
    )
    {
        double size = line.Size * Scale;
        int thickness = line.Thickness * Scale;
        int left =
            origin.X
            + Cv2.GetTextSize(line.Text.Substring(0, index), line.Font, size, thickness, out _).Width;
        int width = Cv2.GetTextSize(line.Text.Substring(index, 1), line.Font, size, thickness, out _).Width;
        return (left, width);
    }

    private static void Mottle(
        Mat m,
        (string Name, string Text, HersheyFonts Font, double Size, int Thickness, Point Origin) line,
        Point origin,
        int index,
        Random random
    )
    {
        var (left, width) = Span(line, origin, index);
        int top = origin.Y - 30 * Scale;
        for (int k = 0; k < 120; k++)
        {
            var p = new Point(left + random.Next(0, width), top + random.Next(0, 32 * Scale));
            if (p.X >= 0 && p.Y >= 0 && p.X < m.Cols && p.Y < m.Rows && m.At<byte>(p.Y, p.X) < 128)
            {
                Cv2.Circle(m, p, 5, Scalar.All(215), -1);
            }
        }
    }
}
