using System.Collections.Generic;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>已配准、已分割的一张标签图。</summary>
internal sealed class LoadedImage
{
    internal LoadedImage(
        BenchmarkImage source,
        string stem,
        ImageFrame frame,
        IReadOnlyList<LoadedLine> lines
    )
    {
        Source = source;
        Stem = stem;
        Frame = frame;
        Lines = lines;
    }

    internal BenchmarkImage Source { get; }

    internal string Stem { get; }

    internal ImageFrame Frame { get; }

    internal IReadOnlyList<LoadedLine> Lines { get; }

    /// <summary>测试时该行第<paramref name = "position"/>位（从1开始）的真值：good、defect或unmarked。</summary>
    internal string Truth(string line, int position, bool heldOutGood)
    {
        if (heldOutGood || Source.Role == "good")
        {
            return "good";
        }

        return
            Source.Defects != null
            && Source.Defects.TryGetValue(line, out var marked)
            && System.Array.IndexOf(marked, position) >= 0
            ? "defect"
            : "unmarked";
    }
}
