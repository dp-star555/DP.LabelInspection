using System;
using System.IO;
using Newtonsoft.Json;

namespace DP.LabelInspection.AnomalyBenchmark;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length == 2 && args[0] == "synth")
            {
                SyntheticLabels.Write(args[1]);
                Console.WriteLine($"已生成合成标签与配置：{Path.Combine(args[1], "bench.json")}");
                return 0;
            }

            if (args.Length == 3 && args[0] == "export")
            {
                if (
                    Directory.Exists(args[2])
                    && Directory.EnumerateFileSystemEntries(args[2]).GetEnumerator().MoveNext()
                )
                {
                    throw new IOException("请指定新的（或空的）输出目录：" + args[2]);
                }

                var config =
                    JsonConvert.DeserializeObject<BenchmarkConfig>(File.ReadAllText(args[1]))
                    ?? throw new ArgumentException("配置为空。");
                Directory.CreateDirectory(args[2]);
                File.Copy(args[1], Path.Combine(args[2], "bench.json"));
                new BenchmarkExporter(config, Path.GetDirectoryName(Path.GetFullPath(args[1]))!).Run(args[2]);
                Console.WriteLine(
                    $"已导出：{Path.Combine(args[2], "index.csv")}，方法B结果：results/dp-b.csv"
                );
                return 0;
            }

            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine(
                "  AnomalyBenchmark synth <new-directory>              生成合成标签与bench.json（流程自检）"
            );
            Console.Error.WriteLine(
                "  AnomalyBenchmark export <bench.json> <new-output>    导出字符数据集并运行方法B"
            );
            return 2;
        }
        catch (Exception e) when (e is IOException || e is ArgumentException || e is JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
