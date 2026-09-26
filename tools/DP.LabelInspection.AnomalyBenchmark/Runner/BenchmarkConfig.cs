using System.Collections.Generic;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>逐字符异常检测对比评估的配置（JSON）。</summary>
internal sealed class BenchmarkConfig
{
    /// <summary>图像目录，相对于配置文件所在目录；缺省为配置文件目录。</summary>
    public string? ImageRoot { get; set; }

    /// <summary>参考图（坐标系与<see cref = "BenchmarkLine.Box"/>一致）；设置时其余图先整体配准到参考图，再逐行微调位置。缺省时按原坐标取行。</summary>
    public string? Reference { get; set; }

    /// <summary>要评估的文字行。</summary>
    public List<BenchmarkLine> Lines { get; set; } = new List<BenchmarkLine>();

    /// <summary>标签图。</summary>
    public List<BenchmarkImage> Images { get; set; } = new List<BenchmarkImage>();

    /// <summary>是否另做留一法：每张训练良品轮流不参与训练、作为良品测试（训练良品至少3张时）。</summary>
    public bool LeaveOneOut { get; set; } = true;

    /// <summary>方法B的位置相关搜索半径。</summary>
    public int LocalRadius { get; set; } = 1;

    /// <summary>阈值 = 留一法最大得分 × 此余量（所有方法相同）。</summary>
    public double ThresholdMargin { get; set; } = 1.5;

    /// <summary>方法B每个字符的样本上限。</summary>
    public int MaximumSamples { get; set; } = 16;
}
