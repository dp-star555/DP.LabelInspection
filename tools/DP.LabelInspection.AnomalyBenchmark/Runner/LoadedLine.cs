using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>一张图上分割成功的一行。</summary>
internal sealed class LoadedLine
{
    internal LoadedLine(BenchmarkLine line, PixelRect roi, CharacterSegmentation segmentation)
    {
        Line = line;
        Roi = roi;
        Segmentation = segmentation;
    }

    internal BenchmarkLine Line { get; }

    internal PixelRect Roi { get; }

    internal CharacterSegmentation Segmentation { get; }

    internal string Group => string.IsNullOrWhiteSpace(Line.Group) ? Line.Name : Line.Group!;
}
