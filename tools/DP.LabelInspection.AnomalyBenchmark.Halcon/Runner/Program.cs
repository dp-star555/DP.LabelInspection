using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HalconDotNet;

namespace DP.LabelInspection.AnomalyBenchmark.Halcon;

/// <summary>
/// HALCON变差模型（train_variation_model，'standard'模式）对导出的字符图评分，结果格式与其他方法相同（results/halcon-variation.csv）。
/// 每个模型键单独建模；训练字符按整张图留一评分，测试字符用该轮全部训练字符建模。
/// 字符得分 = min(±shift平移) max(像素) mean_image(|I − 均值| / max(标准差, floor))，
/// 即prepare_variation_model(AbsThreshold = floor·V, VarThreshold = V)时恰好开始报缺陷的V。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "Usage: AnomalyBenchmark.Halcon <导出目录> [--floor 8] [--smooth 3] [--shift 1] [--name halcon-variation]"
            );
            return 2;
        }

        try
        {
            string data = args[0];
            double floor = Option(args, "--floor", 8);
            int smooth = (int)Option(args, "--smooth", 3);
            int shift = (int)Option(args, "--shift", 1);
            string name = args.SkipWhile(a => a != "--name").Skip(1).FirstOrDefault() ?? "halcon-variation";
            HOperatorSet.GetSystem("version", out HTuple version);
            Console.WriteLine("HALCON " + version.S);
            try
            {
                // 字符图路径可能含中文（组名、行名）。
                HOperatorSet.SetSystem("filename_encoding", "utf8");
            }
            catch (HOperatorException)
            {
                // 旧版本没有该参数，保持默认。
            }

            var rows = ReadCsv(Path.Combine(data, "index.csv"));
            var sets = rows.GroupBy(r => (r["run"], r["key"])).ToList();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            string output = Path.Combine(data, "results", name + ".csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = new StreamWriter(output, false, new UTF8Encoding(true));
            writer.WriteLine("run,id,score");
            foreach (var set in sets)
            {
                var train = set.Where(r => r["split"] == "train").ToList();
                var test = set.Where(r => r["split"] == "test").ToList();
                if (test.Count > 0)
                {
                    using var model = new VariationModel(
                        train.Select(r => Path.Combine(data, r["path"])),
                        floor
                    );
                    foreach (var r in test)
                    {
                        Write(writer, r, model.Score(Path.Combine(data, r["path"]), smooth, shift));
                        n++;
                    }
                }

                var images = train.GroupBy(r => r["image"]).ToList();
                if (images.Count < 2)
                {
                    continue;
                }

                foreach (var held in images)
                {
                    using var model = new VariationModel(
                        train.Where(r => r["image"] != held.Key).Select(r => Path.Combine(data, r["path"])),
                        floor
                    );
                    foreach (var r in held)
                    {
                        Write(writer, r, model.Score(Path.Combine(data, r["path"]), smooth, shift));
                        n++;
                    }
                }
            }

            Console.WriteLine($"{name}: {n} scores in {watch.Elapsed.TotalSeconds:F1} s -> {output}");
            return 0;
        }
        catch (Exception e) when (e is IOException || e is HOperatorException || e is ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static void Write(StreamWriter writer, Dictionary<string, string> row, double score)
    {
        writer.WriteLine(
            string.Join(
                ",",
                new[] { row["run"], row["id"], score.ToString("R", CultureInfo.InvariantCulture) }.Select(
                    Quote
                )
            )
        );
    }

    private static string Quote(string s)
    {
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static double Option(string[] args, string name, double fallback)
    {
        string? value = args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return value == null ? fallback : double.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>读取导出器写的CSV（UTF-8，字段含逗号或引号时加引号）。</summary>
    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var header = Split(lines[0].TrimStart('﻿'));
        return lines
            .Skip(1)
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                var fields = Split(l);
                return header
                    .Select((h, i) => (h, v: i < fields.Count ? fields[i] : ""))
                    .ToDictionary(p => p.h, p => p.v);
            })
            .ToList();
    }

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
