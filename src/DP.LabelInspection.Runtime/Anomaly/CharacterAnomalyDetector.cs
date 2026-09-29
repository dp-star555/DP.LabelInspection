using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 逐字符局部块异常检测（质量方法B的逐字符模式）的业务组合，用于内容可变的文字：算子为DP.Vision的
/// <see cref = "ICharacterAnomalyDetector"/>（字符按行几何归一化、局部块比较、缺墨检查）；本类只负责
/// 模型键（字符组/字符，区分大小写的字母/数字）、库条目、组内阈值下限和报告措辞。
/// 字符身份与分割来自OCR和文字质量的分割（或显式等格）。
/// </summary>
public sealed class CharacterAnomalyDetector
{
    private readonly IPatchAnomalyDetector _algorithm;

    /// <summary>使用手工特征实现。</summary>
    public CharacterAnomalyDetector()
        : this(new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector()) { }

    /// <summary>使用宿主提供的训练实现（例如CNN骨干网络特征），宿主拥有。</summary>
    /// <param name = "algorithm">局部块异常检测实现。</param>
    public CharacterAnomalyDetector(IPatchAnomalyDetector algorithm)
    {
        _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <summary>单元内位置相关搜索半径（归一化像素，1–16），默认1：字符已按行几何和分割中心归一化，更大的半径在实拍标签上只增加误报和耗时。</summary>
    public int LocalRadius { get; set; } = 1;

    /// <summary>自动阈值相对良品留一法最大得分的倍数（1–5），默认1.5。</summary>
    public double ThresholdMargin { get; set; } = 1.5;

    /// <summary>每个字符最多使用的训练样本数，超出时按形态多样性选取，默认16（检测耗时与样本数成正比）。</summary>
    public int MaximumSamples { get; set; } = 16;

    /// <summary>按字符汇总各行样本并训练，每个字符一个<see cref = "EAnomalyModelScope.Character"/>条目，按字符排序。</summary>
    /// <param name = "lines">良品行样本，字符身份须已确认。</param>
    /// <param name = "token">协作式取消标记。</param>
    public IReadOnlyList<AnomalyModelEntry> Train(
        IReadOnlyList<CharacterAnomalySample> lines,
        CancellationToken token = default
    )
    {
        if (lines == null || lines.Count == 0)
        {
            throw new ArgumentException("Good lines are required.", nameof(lines));
        }

        if (MaximumSamples < 1)
        {
            throw new InvalidOperationException("MaximumSamples must be positive.");
        }

        using var vision = new VisionLines(lines);
        var trained = Operator()
            .Train(
                vision.Lines,
                new CharacterAnomalyOptions(ThresholdMargin, LocalRadius, MaximumSamples),
                token
            );
        var entries = trained
            .Select(t =>
            {
                var bytes = t.Model.ToBytes();
                return new AnomalyModelEntry(
                    t.Key,
                    bytes,
                    Sha256(bytes),
                    t.Model.FeatureSource,
                    t.CellWidth,
                    t.CellHeight,
                    t.Model.Radius,
                    t.Samples,
                    t.Model.Threshold,
                    0,
                    t.Options.Stride,
                    t.Options.MinimumArea,
                    t.Calibration,
                    EAnomalyModelScope.Character,
                    t.InkThreshold
                );
            })
            .ToList();
        return Floor(entries).AsReadOnly();
    }

    /// <summary>
    /// 按训练时的方式归一化字符单元，供与其他异常检测算法在相同输入上对比：每个键的单元宽度由训练样本决定（与<see cref = "Train"/>一致），
    /// 其他样本按同一宽度归一化；训练样本中没有的键不输出。不做样本数上限选取。
    /// </summary>
    /// <param name = "training">训练行样本。</param>
    /// <param name = "others">其他（测试）行样本，序号接在训练样本之后。</param>
    /// <param name = "token">协作式取消标记。</param>
    public IReadOnlyList<CharacterAnomalyCell> NormalizeCells(
        IReadOnlyList<CharacterAnomalySample> training,
        IReadOnlyList<CharacterAnomalySample> others,
        CancellationToken token = default
    )
    {
        if (training == null || others == null)
        {
            throw new ArgumentNullException(training == null ? nameof(training) : nameof(others));
        }

        using var vision = new VisionLines(training.Concat(others).ToArray());
        var cells = Operator()
            .NormalizeCells(
                vision.Lines.Take(training.Count).ToArray(),
                vision.Lines.Skip(training.Count).ToArray(),
                token
            );
        try
        {
            return cells
                .Select(c => new CharacterAnomalyCell(
                    c.Line,
                    c.Index,
                    c.Key,
                    c.Training,
                    Bridge.ToLabel(c.Image)
                ))
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            foreach (var c in cells)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>借用Vision原图执行逐字异常检测，不生成旧标签图像快照。</summary>
    /// <param name="image">借用原图。</param>
    /// <param name="characters">原图坐标中的字符及身份。</param>
    /// <param name="crop">热力图范围。</param>
    /// <param name="models">固定版本字符模型查询。</param>
    /// <param name="token">协作式取消。</param>
    /// <param name="inkLoss">是否执行可用的缺墨检查。</param>
    /// <returns>原图坐标的逐字证据。</returns>
    public CharacterAnomalyResult Inspect(
        DP.Vision.IImageSource image,
        IReadOnlyList<DP.LabelInspection.Contracts.CharacterPatch> characters,
        PixelRect crop,
        Func<string, CharacterAnomalyModel?> models,
        CancellationToken token = default,
        bool inkLoss = true
    )
    {
        if (image == null || characters == null || models == null)
            throw new ArgumentNullException(image == null ? nameof(image) : nameof(characters));
        using var result = Operator()
            .Inspect(
                image,
                characters.Select(c => ToVision(c, Alphanumeric(c) ? c.Character : null)).ToArray(),
                crop,
                key => models(key)?.Reference,
                inkLoss,
                token
            );
        return new CharacterAnomalyResult(
            result.Crop,
            result.Characters.Select(Score),
            result.HeatMap == null ? null : Bridge.ToLabel(result.HeatMap)
        );
    }

    /// <summary>Vision逐字结果转为报告：各证据前加“字符[x]（第n位）：”。</summary>
    private static CharacterAnomalyScore Score(CharacterAnomalyOutcome o)
    {
        var c = o.Character;
        string where = $"字符[{c.Character}]（第{c.TokenIndex + 1}位）：";
        return new CharacterAnomalyScore(
            c.Character,
            c.TokenIndex,
            c.Bounds,
            o.Status switch
            {
                ECharacterAnomalyStatus.Compared => "compared",
                ECharacterAnomalyStatus.MissingModel => "missing_model",
                ECharacterAnomalyStatus.Unmeasurable => "unmeasurable",
                _ => "blocked",
            },
            o.MaximumScore,
            o.Threshold,
            o.Findings.Select(f =>
                Bridge.ToLabel(new QualityFinding(f.Code, where + f.Message, f.Kind, f.Bounds, f.AreaPixels))
            ),
            o.InkLoss,
            o.InkThreshold
        );
    }

    private ICharacterAnomalyDetector Operator()
    {
        return new DP.Vision.OpenCv.OpenCvCharacterAnomalyDetector(_algorithm);
    }

    private static bool Alphanumeric(DP.LabelInspection.Contracts.CharacterPatch c)
    {
        return c.Character.Length == 1 && FieldSettings.IsAlphanumeric(c.Character[0]);
    }

    private static CharacterAnomalyCharacter ToVision(
        DP.LabelInspection.Contracts.CharacterPatch c,
        string? key
    )
    {
        return new CharacterAnomalyCharacter(c.Character, c.TokenIndex, c.Bounds, key);
    }

    /// <summary>标签行样本转为Vision行：同一标签图对象只转换一次（同一来源），未排除的字母/数字带“组/字符”模型键。</summary>
    private sealed class VisionLines : IDisposable
    {
        private readonly Dictionary<PixelSnapshot, DP.Vision.IImageSource> _images =
            new Dictionary<PixelSnapshot, DP.Vision.IImageSource>();

        internal VisionLines(IReadOnlyList<CharacterAnomalySample> samples)
        {
            Lines = samples
                .Select(s =>
                {
                    if (!_images.TryGetValue(s.Image, out var image))
                    {
                        image = _images[s.Image] = Bridge.ToVision(s.Image);
                    }

                    return new CharacterAnomalyLine(
                        image,
                        s.Characters.Select(
                            (c, i) =>
                                ToVision(
                                    c,
                                    !s.Excluded.Contains(i) && Alphanumeric(c)
                                        ? AnomalyModelEntry.CharacterKey(s.Group, c.Character)
                                        : null
                                )
                        )
                    );
                })
                .ToArray();
        }

        internal IReadOnlyList<CharacterAnomalyLine> Lines { get; }

        public void Dispose()
        {
            foreach (var image in _images.Values)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// 阈值下限：样本少的字符留一法阈值不可靠（实拍标签上误报集中在2–4个样本的字符），
    /// 同一字体各字符阈值相近，因此每个字符的阈值取自身阈值与本批各字符阈值中位数的较大者（至少5个字符时）。
    /// </summary>
    private static List<AnomalyModelEntry> Floor(List<AnomalyModelEntry> entries)
    {
        // 按字符组分别取中位数：不同字体的组阈值水平不同；字符少于5种的组（如“300”）用本批全部字符的中位数。
        double all = entries.Count >= 5 ? Median(entries) : double.NaN;
        return entries
            .GroupBy(e => e.Group ?? "")
            .SelectMany(g => Floor(g.ToList(), g.Count() >= 5 ? Median(g) : all))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static double Median(IEnumerable<AnomalyModelEntry> entries)
    {
        var sorted = entries.Select(e => e.Threshold).OrderBy(t => t).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static IEnumerable<AnomalyModelEntry> Floor(List<AnomalyModelEntry> entries, double median)
    {
        if (double.IsNaN(median))
        {
            return entries;
        }

        return entries.Select(e =>
            e.Threshold >= median
                ? e
                : new AnomalyModelEntry(
                    e.Key,
                    e.CopyModel(),
                    e.Sha256,
                    e.FeatureSource,
                    e.Width,
                    e.Height,
                    e.LocalRadius,
                    e.TrainingImages,
                    median,
                    e.Margin,
                    e.Stride,
                    e.MinimumArea,
                    e.Calibration + $"；阈值取各字符阈值中位数{median:F3}（本字符{e.Threshold:F3}）",
                    e.Scope,
                    e.InkThreshold
                )
        );
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
