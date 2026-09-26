using System.Collections.Generic;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>评估配置中的一张标签图。</summary>
internal sealed class BenchmarkImage
{
    /// <summary>图像文件（相对于<see cref = "BenchmarkConfig.ImageRoot"/>）。</summary>
    public string File { get; set; } = "";

    /// <summary>train（训练良品）、good（不参与训练的良品，测误报）或defect（有缺陷的标签）。</summary>
    public string Role { get; set; } = "train";

    /// <summary>按行名称覆盖文本（如每张不同的序列号）；空字符串表示本图不评估该行。</summary>
    public Dictionary<string, string>? Texts { get; set; }

    /// <summary>defect图中有缺陷的字符：行名称 → 位序（从1开始，与检测结果“第n位”一致）。未标注的字符记为unmarked，不计入误报。</summary>
    public Dictionary<string, int[]>? Defects { get; set; }
}
