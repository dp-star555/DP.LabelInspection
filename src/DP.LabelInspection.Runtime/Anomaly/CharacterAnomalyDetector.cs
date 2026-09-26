using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using OpenCvSharp;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 逐字符局部块异常检测（质量方法B的逐字符模式），用于内容可变的文字：每个字符（区分大小写的字母/数字）一个模型，
/// 用多个良品样本训练。字符按整行几何归一化（大写高度缩放到32像素，见<see cref = "CharacterCells"/>），
/// 在单元内按位置相关模式（默认±1像素）与同一字符的良品比较，因此相邻字符与字符间距的变化互不影响。
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

        var grays = new Dictionary<ImageFrame, Mat>();
        try
        {
            var samples = new List<(string key, Mat gray, CharacterLine line, PixelRect cell)>();
            foreach (var sample in lines)
            {
                token.ThrowIfCancellationRequested();
                if (!grays.TryGetValue(sample.Image, out var gray))
                {
                    gray = grays[sample.Image] = Gray(sample.Image);
                }

                var line = CharacterCells.Measure(gray, sample.Characters);
                if (line == null)
                {
                    continue;
                }

                for (int i = 0; i < sample.Characters.Count; i++)
                {
                    var c = sample.Characters[i];
                    if (
                        !sample.Excluded.Contains(i)
                        && c.Character.Length == 1
                        && FieldSettings.IsAlphanumeric(c.Character[0])
                    )
                    {
                        samples.Add(
                            (AnomalyModelEntry.CharacterKey(sample.Group, c.Character), gray, line, c.Bounds)
                        );
                    }
                }
            }

            if (samples.Count == 0)
            {
                throw new ArgumentException("No measurable alphanumeric characters in the good lines.");
            }

            var options = new PatchAnomalyOptions(thresholdMargin: ThresholdMargin, localRadius: LocalRadius);
            var entries = new List<AnomalyModelEntry>();
            foreach (var group in samples.GroupBy(s => s.key).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var all = group.ToArray();
                int width = all.Max(s => CharacterCells.Width(s.cell.Width, s.line));
                var chosen = all.Length <= MaximumSamples ? all : Diverse(all, width);
                var crops = new List<DP.Vision.IImageSource>();
                try
                {
                    foreach (var s in chosen)
                    {
                        using var cell = CharacterCells.Normalize(s.gray, s.line, s.cell, width);
                        crops.Add(Bridge.ToVision(CvImages.Frame(cell.Image)));
                    }

                    var model = _algorithm.Train(crops, options, token);
                    var bytes = model.ToBytes();
                    entries.Add(
                        new AnomalyModelEntry(
                            group.Key,
                            bytes,
                            Sha256(bytes),
                            model.FeatureSource,
                            width,
                            CharacterCells.CellHeight,
                            model.Radius,
                            chosen.Length,
                            model.Threshold,
                            0,
                            options.Stride,
                            options.MinimumArea,
                            model.Calibration,
                            EAnomalyModelScope.Character
                        )
                    );
                }
                finally
                {
                    foreach (var c in crops)
                    {
                        c.Dispose();
                    }
                }
            }

            return Floor(entries).AsReadOnly();
        }
        finally
        {
            foreach (var g in grays.Values)
            {
                g.Dispose();
            }
        }
    }

    /// <summary>检测一行：逐个字母/数字字符与其模型比较，异常区域以原图坐标报告。</summary>
    /// <param name = "image">整张待检图。</param>
    /// <param name = "characters">该行字符（原图坐标，身份来自OCR或等格声明）。</param>
    /// <param name = "crop">热力图对应的原图范围，通常为ROI。</param>
    /// <param name = "models">按字符查找模型；没有时返回null。</param>
    /// <param name = "token">协作式取消标记。</param>
    public CharacterAnomalyResult Inspect(
        ImageFrame image,
        IReadOnlyList<DP.LabelInspection.Contracts.CharacterPatch> characters,
        PixelRect crop,
        Func<string, CharacterAnomalyModel?> models,
        CancellationToken token = default
    )
    {
        if (image == null || characters == null || models == null)
        {
            throw new ArgumentNullException(image == null ? nameof(image) : nameof(characters));
        }

        using var gray = Gray(image);
        var line = CharacterCells.Measure(gray, characters);
        var region = new Rect(crop.X, crop.Y, crop.Width, crop.Height) & new Rect(0, 0, gray.Cols, gray.Rows);
        using var heat = new Mat(
            Math.Max(1, region.Height),
            Math.Max(1, region.Width),
            MatType.CV_8UC1,
            Scalar.All(0)
        );
        bool anyHeat = false;
        var scores = new List<CharacterAnomalyScore>();
        foreach (var c in characters)
        {
            token.ThrowIfCancellationRequested();
            if (c.Character.Length != 1 || !FieldSettings.IsAlphanumeric(c.Character[0]))
            {
                continue;
            }

            string where = $"字符[{c.Character}]（第{c.TokenIndex + 1}位）";
            CharacterAnomalyScore Stop(string status, string code, string message)
            {
                return new CharacterAnomalyScore(
                    c.Character,
                    c.TokenIndex,
                    c.Bounds,
                    status,
                    0,
                    0,
                    new[]
                    {
                        new InspectionFinding(code, where + "：" + message, EInspectionVerdict.Ng, c.Bounds),
                    }
                );
            }

            var model = models(c.Character);
            if (model == null)
            {
                scores.Add(
                    Stop(
                        "missing_model",
                        "anomaly_character_model_missing",
                        "异常模型库中没有该字符的模型，无法判断；请用含该字符的良品补充训练。"
                    )
                );
                continue;
            }

            if (line == null)
            {
                scores.Add(Stop("unmeasurable", "anomaly_line_unmeasurable", "无法测量该行的字高与基线。"));
                continue;
            }

            if (model.Entry.Height != CharacterCells.CellHeight)
            {
                scores.Add(
                    Stop(
                        "blocked",
                        "patch_anomaly_model_mismatch",
                        "字符模型的归一化尺寸与当前实现不一致，需重新训练。"
                    )
                );
                continue;
            }

            using var cell = CharacterCells.Normalize(gray, line, c.Bounds, model.Entry.Width);
            using var source = Bridge.ToVision(CvImages.Frame(cell.Image));
            using var result = model.Detector.Detect(
                source,
                model.Model,
                RegionAnomalyDetector.DetectionOptions(model.Entry, model.Model),
                token
            );
            var findings = result
                .Findings.Select(f =>
                    f.Bounds is { } b
                        ? new QualityFinding(
                            f.Code,
                            where + "：" + f.Message,
                            f.Kind,
                            Bridge.ToVision(cell.ToImage(b.X, b.Y, b.Width, b.Height)),
                            f.AreaPixels is { } area
                                ? Math.Max(1, (int)Math.Round(area / (cell.Scale * cell.Scale)))
                                : (int?)null
                        )
                        : new QualityFinding(f.Code, where + "：" + f.Message, f.Kind)
                )
                .Select(Bridge.ToLabel)
                .ToList();
            bool completed = result.Status == EAlgorithmStatus.Completed;
            scores.Add(
                new CharacterAnomalyScore(
                    c.Character,
                    c.TokenIndex,
                    c.Bounds,
                    completed ? "compared" : "blocked",
                    result.MaximumScore,
                    result.Threshold,
                    findings
                )
            );
            if (result.HeatMap != null && region.Width > 0 && region.Height > 0)
            {
                using var local = CvImages.Mat(Bridge.ToLabel(result.HeatMap));
                using var back = new Mat(2, 3, MatType.CV_64FC1);
                back.Set(0, 0, 1 / cell.Scale);
                back.Set(0, 1, 0.0);
                back.Set(0, 2, cell.X0 - region.X);
                back.Set(1, 0, 0.0);
                back.Set(1, 1, 1 / cell.Scale);
                back.Set(1, 2, cell.Y0 - region.Y);
                using var mapped = new Mat();
                Cv2.WarpAffine(
                    local,
                    mapped,
                    back,
                    heat.Size(),
                    InterpolationFlags.Linear,
                    BorderTypes.Constant,
                    Scalar.All(0)
                );
                Cv2.Max(heat, mapped, heat);
                anyHeat = true;
            }
        }

        return new CharacterAnomalyResult(
            new PixelRect(region.X, region.Y, region.Width, region.Height),
            scores,
            anyHeat ? CvImages.Frame(heat) : null
        );
    }

    /// <summary>
    /// 样本多于上限时按形态多样性选取（贪心最远点）：先取最接近其余样本的一个，再依次取与已选样本最不相似的。
    /// 按顺序等间隔抽取会漏掉少数形态不同的良品（例如另一行字号略不同的同一字符），检测时这些良品会被误报；
    /// 实拍标签上“WF675907”中的“0”即如此。超过400个样本时先等间隔抽到400个再选。
    /// </summary>
    private (string key, Mat gray, CharacterLine line, PixelRect cell)[] Diverse(
        (string key, Mat gray, CharacterLine line, PixelRect cell)[] all,
        int width
    )
    {
        var pool =
            all.Length <= 400
                ? all
                : Enumerable.Range(0, 400).Select(i => all[(int)((long)i * all.Length / 400)]).ToArray();
        var vectors = pool.Select(s =>
            {
                using var cell = CharacterCells.Normalize(s.gray, s.line, s.cell, width);
                using var small = new Mat();
                // 缩到1/2比较整体形态，对细微噪声不敏感。
                Cv2.Resize(
                    cell.Image,
                    small,
                    new Size(width / 2, CharacterCells.CellHeight / 2),
                    0,
                    0,
                    InterpolationFlags.Area
                );
                var bytes = new byte[small.Rows * small.Cols];
                System.Runtime.InteropServices.Marshal.Copy(small.Data, bytes, 0, bytes.Length);
                return bytes.Select(b => (float)b).ToArray();
            })
            .ToArray();
        int n = vectors.Length;
        double Distance(int i, int j)
        {
            double sum = 0;
            var a = vectors[i];
            var b = vectors[j];
            for (int k = 0; k < a.Length; k++)
            {
                double t = a[k] - b[k];
                sum += t * t;
            }

            return sum;
        }

        int first = Enumerable
            .Range(0, n)
            .OrderBy(i => Enumerable.Range(0, n).Sum(j => j == i ? 0 : Math.Sqrt(Distance(i, j))))
            .First();
        var selected = new List<int> { first };
        var nearest = Enumerable.Range(0, n).Select(i => Distance(i, first)).ToArray();
        while (selected.Count < MaximumSamples)
        {
            int next = Enumerable.Range(0, n).OrderByDescending(i => nearest[i]).First();
            if (nearest[next] <= 0)
            {
                break;
            }

            selected.Add(next);
            for (int i = 0; i < n; i++)
            {
                nearest[i] = Math.Min(nearest[i], Distance(i, next));
            }
        }

        return selected.OrderBy(i => i).Select(i => pool[i]).ToArray();
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
                    e.Scope
                )
        );
    }

    private static Mat Gray(ImageFrame frame)
    {
        var raw = CvImages.Mat(frame);
        if (frame.Format == EImagePixelFormat.Gray8)
        {
            return raw;
        }

        using (raw)
        {
            var gray = new Mat();
            Cv2.CvtColor(raw, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
