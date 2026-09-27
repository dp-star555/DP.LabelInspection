namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>评估配置中的一行文字（对应一个文字ROI）。</summary>
internal sealed class BenchmarkLine
{
    /// <summary>行名称（ROI名称），在各图的文本覆盖与缺陷标注中引用。</summary>
    public string Name { get; set; } = "";

    /// <summary>ROI：x, y, 宽, 高（参考图坐标）。</summary>
    public int[] Box { get; set; } = new int[0];

    /// <summary>默认文本；各图可在<see cref = "BenchmarkImage.Texts"/>中覆盖（如序列号）。</summary>
    public string Text { get; set; } = "";

    /// <summary>字符组，缺省为行名称（每行一组）；同字体的几行可填相同的组。</summary>
    public string? Group { get; set; }
}
