using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>
/// 异常模型批量训练（质量方法B）的与UI无关的采集会话：载入多张良品图，在每张图上为若干模型画样本框，
/// 采集完成后一次训练全部模型并作为一个版本发布。
/// <list type = "bullet">
/// <item>内容固定模型：样本框尺寸由第一个样本（或对应配方ROI）决定；后加的框自动取同尺寸，并在±<see cref = "SnapRadius"/>像素内
/// 按与第一个样本的归一化相关对齐，手画粗略即可。</item>
/// <item>内容可变模型：框的尺寸和位置不限。</item>
/// <item>逐字符模型：框住整行，提取时按OCR（或确认文本）自动分割为字符；可修改身份、取消个别字符。</item>
/// </list>
/// </summary>
public sealed class AnomalyTrainingSession
{
    private readonly List<AnomalyTrainingImage> _images = new List<AnomalyTrainingImage>();
    private readonly List<AnomalyTrainingModel> _models = new List<AnomalyTrainingModel>();
    private readonly List<AnomalyTrainingSample> _samples = new List<AnomalyTrainingSample>();
    private readonly Dictionary<ImageFrame, byte[]> _gray = new Dictionary<ImageFrame, byte[]>();
    private readonly HashSet<string> _dismissed = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>内容固定模型样本自动对齐的搜索半径（原图像素），0–64，默认16；0表示不对齐。</summary>
    public int SnapRadius { get; set; } = 16;

    /// <summary>已载入的良品图。</summary>
    public IReadOnlyList<AnomalyTrainingImage> Images => _images.AsReadOnly();

    /// <summary>模型。</summary>
    public IReadOnlyList<AnomalyTrainingModel> Models => _models.AsReadOnly();

    /// <summary>全部样本框，按添加顺序。</summary>
    public IReadOnlyList<AnomalyTrainingSample> Samples => _samples.AsReadOnly();

    /// <summary>载入一张良品图。</summary>
    /// <param name = "image">不可变整图。</param>
    /// <param name = "name">显示名称。</param>
    /// <param name = "path">来源文件路径，可为null。</param>
    public AnomalyTrainingImage AddImage(ImageFrame image, string name, string? path = null)
    {
        var item = new AnomalyTrainingImage(image, name, path);
        _images.Add(item);
        return item;
    }

    /// <summary>移除一张图及其上的全部样本框。</summary>
    /// <param name = "image">要移除的图。</param>
    public void RemoveImage(AnomalyTrainingImage image)
    {
        _samples.RemoveAll(s => s.Image == image);
        _images.Remove(image);
        _gray.Remove(image.Image);
    }

    /// <summary>新建模型。</summary>
    /// <param name = "name">模型名称（模型键），1–100字符，唯一。</param>
    /// <param name = "kind">训练方式。</param>
    /// <param name = "region">对应的配方ROI（同名时发布后可一键绑定），可为null。</param>
    public AnomalyTrainingModel AddModel(
        string name,
        EAnomalyTrainingKind kind,
        InspectionRegion? region = null
    )
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            throw new ArgumentException("模型名称须为1–100字符。", nameof(name));
        }

        if (_models.Any(m => m.Name == name))
        {
            throw new ArgumentException("模型名称已存在：" + name, nameof(name));
        }

        if (!Enum.IsDefined(typeof(EAnomalyTrainingKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Check(kind, region);
        var model = new AnomalyTrainingModel(name, kind, region);
        if (kind == EAnomalyTrainingKind.FixedContent && region != null)
        {
            model.Width = region.Bounds.Width;
            model.Height = region.Bounds.Height;
        }

        _models.Add(model);
        return model;
    }

    /// <summary>
    /// 为配方中尚未建模的ROI各建一个同名模型：固定/空白区及有固定引导值的文字为内容固定，其余文字为逐字符，
    /// 码为内容可变（内容固定的码可再改为内容固定）。忽略区跳过。
    /// </summary>
    /// <param name = "regions">配方ROI。</param>
    /// <returns>新建的模型。</returns>
    public IReadOnlyList<AnomalyTrainingModel> ImportRecipe(IEnumerable<InspectionRegion> regions)
    {
        var added = new List<AnomalyTrainingModel>();
        foreach (var r in regions ?? throw new ArgumentNullException(nameof(regions)))
        {
            if (r.Kind == ERegionKind.Ignore || _models.Any(m => m.Name == r.Name))
            {
                continue;
            }

            var kind =
                r.Kind == ERegionKind.Barcode ? EAnomalyTrainingKind.VariableContent
                : r.Kind == ERegionKind.Text && r.Field.Expected == null && r.SingleLine
                    ? EAnomalyTrainingKind.Characters
                : EAnomalyTrainingKind.FixedContent;
            added.Add(AddModel(r.Name, kind, r));
        }

        return added.AsReadOnly();
    }

    /// <summary>
    /// 与当前配方同步（每次打开训练页时调用）：配方中新增的ROI各建同名模型（人工删除过的同名模型不再自动添加），
    /// 已有模型关联到配方中同名ROI的最新配置（框的位置/尺寸可能已改）；配方中已删除的ROI，其模型保留为独立模型。
    /// </summary>
    /// <param name = "regions">当前配方ROI。</param>
    /// <returns>新建的模型。</returns>
    public IReadOnlyList<AnomalyTrainingModel> SyncRecipe(IEnumerable<InspectionRegion> regions)
    {
        var current = (regions ?? throw new ArgumentNullException(nameof(regions)))
            .Where(r => r.Kind != ERegionKind.Ignore)
            .ToArray();
        var byName = current.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var model in _models)
        {
            if (byName.TryGetValue(model.Name, out var region))
            {
                bool compatible =
                    model.Kind != EAnomalyTrainingKind.Characters || region.Kind == ERegionKind.Text;
                model.Region = compatible ? region : null;
                if (compatible && model.Kind == EAnomalyTrainingKind.FixedContent && model.Width == null)
                {
                    model.Width = region.Bounds.Width;
                    model.Height = region.Bounds.Height;
                }
            }
            else if (model.Region != null)
            {
                model.Region = null;
            }
        }

        return ImportRecipe(current.Where(r => !_dismissed.Contains(r.Name)));
    }

    /// <summary>移除模型及其全部样本框；对应配方ROI的模型之后同步配方时不再自动添加。</summary>
    /// <param name = "model">要移除的模型。</param>
    public void RemoveModel(AnomalyTrainingModel model)
    {
        if (model.Region != null)
        {
            _dismissed.Add(model.Name);
        }

        _samples.RemoveAll(s => s.Model == model);
        _models.Remove(model);
    }

    /// <summary>
    /// 修改训练方式。改为内容固定时，尺寸取第一个样本（或对应ROI），其余样本按中心改为同尺寸；逐字符样本的提取结果被清除。
    /// </summary>
    /// <param name = "model">模型。</param>
    /// <param name = "kind">新的训练方式。</param>
    public void SetKind(AnomalyTrainingModel model, EAnomalyTrainingKind kind)
    {
        if (!Enum.IsDefined(typeof(EAnomalyTrainingKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Check(kind, model.Region);
        if (model.Kind == kind)
        {
            return;
        }

        model.Kind = kind;
        model.Width = model.Height = null;
        var samples = Of(model).ToArray();
        foreach (var s in samples)
        {
            s.ClearExtraction();
        }

        if (kind == EAnomalyTrainingKind.FixedContent)
        {
            var size = samples.Length > 0 ? samples[0].Bounds : model.Region?.Bounds;
            if (size != null)
            {
                Resize(model, size.Value.Width, size.Value.Height);
            }
        }
    }

    /// <summary>内容固定模型改为指定尺寸，全部样本按各自中心改为新尺寸（移入图内）。</summary>
    /// <param name = "model">内容固定模型。</param>
    /// <param name = "width">宽度（原图像素），至少8。</param>
    /// <param name = "height">高度（原图像素），至少8。</param>
    public void SetSize(AnomalyTrainingModel model, int width, int height)
    {
        if (model.Kind != EAnomalyTrainingKind.FixedContent)
        {
            throw new InvalidOperationException("只有内容固定模型有统一尺寸。");
        }

        if (width < 8 || height < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Resize(model, width, height);
    }

    /// <summary>
    /// 在图上为模型添加样本框。内容固定模型：框改为模型尺寸（保持中心），并与第一个样本对齐；第一个样本决定尺寸。
    /// </summary>
    /// <param name = "image">所在图像。</param>
    /// <param name = "model">所属模型。</param>
    /// <param name = "bounds">手画的框（原图坐标）。</param>
    public AnomalyTrainingSample AddSample(
        AnomalyTrainingImage image,
        AnomalyTrainingModel model,
        PixelRect bounds
    )
    {
        Known(image, model);
        var box = Fit(image, model, bounds);
        if (model.Kind == EAnomalyTrainingKind.FixedContent)
        {
            if (model.Width == null)
            {
                model.Width = box.Width;
                model.Height = box.Height;
            }

            box = Snap(image, model, box);
        }

        var sample = new AnomalyTrainingSample(image, model, box);
        _samples.Add(sample);
        return sample;
    }

    /// <summary>
    /// 调整样本框。内容固定模型：尺寸不变时只移动（不再自动对齐）；尺寸改变时该模型全部样本改为新尺寸（保持各自中心）。
    /// 逐字符样本的提取结果被清除，需重新提取。
    /// </summary>
    /// <param name = "sample">样本。</param>
    /// <param name = "bounds">新框。</param>
    public void MoveSample(AnomalyTrainingSample sample, PixelRect bounds)
    {
        if (!_samples.Contains(sample))
        {
            throw new ArgumentException("样本不在本会话中。", nameof(sample));
        }

        var model = sample.Model;
        if (
            model.Kind == EAnomalyTrainingKind.FixedContent
            && (bounds.Width != model.Width || bounds.Height != model.Height)
        )
        {
            Inside(sample.Image, bounds);
            Resize(model, bounds.Width, bounds.Height);
            sample.Bounds = bounds;
            return;
        }

        sample.Bounds = Inside(sample.Image, bounds);
        sample.ClearExtraction();
    }

    /// <summary>删除样本框。</summary>
    /// <param name = "sample">样本。</param>
    public void RemoveSample(AnomalyTrainingSample sample)
    {
        _samples.Remove(sample);
    }

    /// <summary>该图上的样本框，按添加顺序。</summary>
    /// <param name = "image">图像。</param>
    public IReadOnlyList<AnomalyTrainingSample> SamplesOn(AnomalyTrainingImage image)
    {
        return _samples.Where(s => s.Image == image).ToArray();
    }

    /// <summary>该模型的样本框。</summary>
    /// <param name = "model">模型。</param>
    public IReadOnlyList<AnomalyTrainingSample> Of(AnomalyTrainingModel model)
    {
        return _samples.Where(s => s.Model == model).ToArray();
    }

    /// <summary>
    /// 按配方位置在图上放置各对应配方ROI的模型的框（该图上已有该模型的框时跳过）；内容固定模型再自动对齐到第一个样本。
    /// 框超出图像的模型跳过。
    /// </summary>
    /// <param name = "image">图像。</param>
    /// <returns>新增的样本框。</returns>
    public IReadOnlyList<AnomalyTrainingSample> PlaceRecipe(AnomalyTrainingImage image)
    {
        var added = new List<AnomalyTrainingSample>();
        foreach (var model in _models.Where(m => m.Region != null))
        {
            if (_samples.Any(s => s.Image == image && s.Model == model))
            {
                continue;
            }

            var box = model.Region!.Bounds;
            if (model.Kind == EAnomalyTrainingKind.FixedContent && model.Width != null)
            {
                box = Centered(box, model.Width.Value, model.Height!.Value);
            }

            if (box.Fits(image.Image))
            {
                added.Add(AddSample(image, model, box));
            }
        }

        return added.AsReadOnly();
    }

    /// <summary>设置逐字符样本的人工确认文本（提取时代替OCR），并清除已有提取结果。</summary>
    /// <param name = "sample">逐字符样本。</param>
    /// <param name = "text">整行文本；null或空表示使用OCR。</param>
    public void SetConfirmedText(AnomalyTrainingSample sample, string? text)
    {
        sample.ConfirmedText = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        sample.ClearExtraction();
    }

    /// <summary>记录逐字符样本的提取结果；切割需复核时字符默认不作为样本。</summary>
    /// <param name = "sample">逐字符样本。</param>
    /// <param name = "segmentation">分割结果。</param>
    public void SetExtraction(AnomalyTrainingSample sample, CharacterSegmentation segmentation)
    {
        if (sample.Model.Kind != EAnomalyTrainingKind.Characters)
        {
            throw new InvalidOperationException("只有逐字符模型需要提取。");
        }

        if (segmentation.Characters.Count == 0)
        {
            sample.ClearExtraction();
            sample.Problem = "未分割出字符：" + segmentation.Reason;
            return;
        }

        sample.Segmentation = segmentation;
        sample._labels = segmentation.Characters.Select(c => c.Character).ToArray();
        bool clean = segmentation.Status == "provisional" || segmentation.Status == "explicit_cells";
        sample._include = segmentation.Characters.Select(_ => clean).ToArray();
        sample.Problem = clean ? null : "切割需复核，字符默认未勾选：" + segmentation.Reason;
    }

    /// <summary>修改逐字符样本中一个字符的身份。</summary>
    /// <param name = "sample">逐字符样本。</param>
    /// <param name = "index">字符序号。</param>
    /// <param name = "label">单个ASCII字母或数字（区分大小写）。</param>
    public void SetLabel(AnomalyTrainingSample sample, int index, string label)
    {
        if (label == null || label.Length != 1 || !FieldSettings.IsAlphanumeric(label[0]))
        {
            throw new ArgumentException("身份须为单个ASCII字母或数字（区分大小写）。", nameof(label));
        }

        sample._labels[index] = label;
    }

    /// <summary>设置逐字符样本中一个字符是否作为训练样本。</summary>
    /// <param name = "sample">逐字符样本。</param>
    /// <param name = "index">字符序号。</param>
    /// <param name = "include">是否作为样本。</param>
    public void SetInclude(AnomalyTrainingSample sample, int index, bool include)
    {
        sample._include[index] = include;
    }

    /// <summary>
    /// 对全部待提取的逐字符样本调用字符候选服务（OCR+分割，与单字库制库相同），逐个记录结果。
    /// </summary>
    /// <param name = "service">字符候选提取服务。</param>
    /// <param name = "progress">可选进度（已完成数, 总数）。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>本次提取的样本数。</returns>
    public async Task<int> ExtractAsync(
        IGlyphCandidateService service,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken token = default
    )
    {
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        var pending = _samples.Where(s => s.NeedsExtraction).ToArray();
        for (int i = 0; i < pending.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var sample = pending[i];
            var bounds = sample.Bounds;
            try
            {
                var result = await service
                    .ExtractGlyphCandidatesAsync(sample.Image.Image, bounds, sample.ConfirmedText, token)
                    .ConfigureAwait(true);
                // 提取期间框被调整或删除时丢弃结果，由下一轮重新提取。
                if (_samples.Contains(sample) && sample.Bounds.Equals(bounds) && sample.NeedsExtraction)
                {
                    SetExtraction(sample, result.Segmentation);
                }
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                sample.Problem = "提取失败：" + error.Message;
            }

            progress?.Report((i + 1, pending.Length));
        }

        return pending.Length;
    }

    /// <summary>逐字符模型各字符（区分大小写）作为样本的数量，按字符排序。</summary>
    public IReadOnlyList<KeyValuePair<string, int>> CharacterCoverage()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _samples.Where(s => s.Segmentation != null))
        {
            for (int i = 0; i < s._labels.Length; i++)
            {
                if (s._include[i])
                {
                    counts[s._labels[i]] = counts.TryGetValue(s._labels[i], out int n) ? n + 1 : 1;
                }
            }
        }

        return counts.ToArray();
    }

    /// <summary>训练前的问题：没有样本的模型、待提取或提取有问题的逐字符样本。</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var m in _models.Where(m => !_samples.Any(s => s.Model == m)))
        {
            problems.Add($"模型“{m.Name}”没有样本框，训练时跳过");
        }

        foreach (var s in _samples.Where(s => s.Model.Kind == EAnomalyTrainingKind.Characters))
        {
            if (s.NeedsExtraction)
            {
                problems.Add($"{s.Image.Name} / {s.Model.Name}：尚未提取字符");
            }
            else if (s.Problem != null)
            {
                problems.Add($"{s.Image.Name} / {s.Model.Name}：{s.Problem}");
            }
        }

        return problems.AsReadOnly();
    }

    /// <summary>
    /// 一次训练全部有样本的模型：整ROI模型各一个条目（键为模型名称），全部逐字符样本合训一组字符模型。
    /// 返回的条目由调用方用<see cref = "IAnomalyLibraryManager.PutAnomalyModels"/>作为一个版本发布。
    /// </summary>
    /// <param name = "trainer">训练实现。</param>
    /// <param name = "progress">可选进度说明。</param>
    /// <param name = "token">协作式取消标记。</param>
    public IReadOnlyList<AnomalyModelEntry> Train(
        IAnomalyModelTrainer trainer,
        IProgress<string>? progress = null,
        CancellationToken token = default
    )
    {
        if (trainer == null)
        {
            throw new ArgumentNullException(nameof(trainer));
        }

        var entries = new List<AnomalyModelEntry>();
        foreach (var model in _models.Where(m => m.Kind != EAnomalyTrainingKind.Characters))
        {
            token.ThrowIfCancellationRequested();
            var samples = Of(model)
                .Select(s => new RegionAnomalySample(s.Image.Image, s.Bounds, s.Image.Name))
                .ToArray();
            if (samples.Length == 0)
            {
                continue;
            }

            progress?.Report($"训练 {model.Name}（{samples.Length}个样本）…");
            var region =
                model.Region
                ?? new InspectionRegion(
                    model.Name,
                    model.Kind == EAnomalyTrainingKind.FixedContent ? ERegionKind.Fixed : ERegionKind.Barcode,
                    samples[0].Bounds
                );
            entries.Add(
                trainer.TrainSamples(
                    samples,
                    region.WithBounds(samples[0].Bounds),
                    model.Kind == EAnomalyTrainingKind.FixedContent,
                    model.Name,
                    token
                )
            );
        }

        var lines = _samples
            .Where(s =>
                s.Model.Kind == EAnomalyTrainingKind.Characters
                && s.Segmentation != null
                && s._include.Any(i => i)
            )
            .Select(s => s.ToCharacterSample())
            .ToArray();
        if (lines.Length > 0)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"训练字符模型（{lines.Length}行）…");
            entries.AddRange(trainer.TrainCharacters(lines, token));
        }

        if (entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new InvalidOperationException("模型名称与字符模型的键冲突（如名为“A”的模型），请改名。");
        }

        return entries.AsReadOnly();
    }

    /// <summary>
    /// 发布后配方ROI的新配置：对应配方ROI的模型绑定到该版本并启用B；内容固定模型尺寸与配方ROI不同时按中心改为模型尺寸，
    /// 逐字符模型选择逐字符模式。没有样本的模型不绑定。
    /// </summary>
    /// <param name = "libraryId">模型库标识。</param>
    /// <param name = "revision">发布的版本。</param>
    public IReadOnlyList<InspectionRegion> Bindings(string libraryId, int revision)
    {
        var result = new List<InspectionRegion>();
        foreach (var model in _models.Where(m => m.Region != null && _samples.Any(s => s.Model == m)))
        {
            var r = model.Region!;
            if (
                model.Kind == EAnomalyTrainingKind.FixedContent
                && (r.Bounds.Width != model.Width || r.Bounds.Height != model.Height)
            )
            {
                r = r.WithBounds(Centered(r.Bounds, model.Width!.Value, model.Height!.Value));
            }

            var pin =
                model.Kind == EAnomalyTrainingKind.Characters
                    ? new AnomalySettings(libraryId, revision, perCharacter: true)
                    : new AnomalySettings(libraryId, revision, model.Name == r.Name ? null : model.Name);
            result.Add(
                r.WithAnomaly(pin)
                    .WithTasks(new RoiInspectionTasks(r.Tasks.ReadData, r.Tasks.CheckQuality, true))
            );
        }

        return result.AsReadOnly();
    }

    private static void Check(EAnomalyTrainingKind kind, InspectionRegion? region)
    {
        if (region?.Kind == ERegionKind.Ignore)
        {
            throw new ArgumentException("忽略区不能训练异常模型。");
        }

        if (kind == EAnomalyTrainingKind.Characters && region != null && region.Kind != ERegionKind.Text)
        {
            throw new ArgumentException("逐字符模型只能对应文字ROI。");
        }
    }

    private void Known(AnomalyTrainingImage image, AnomalyTrainingModel model)
    {
        if (!_images.Contains(image) || !_models.Contains(model))
        {
            throw new ArgumentException("图像或模型不在本会话中。");
        }
    }

    /// <summary>内容固定模型改为模型尺寸（保持中心）并移入图内；其他模型只检查在图内。</summary>
    private static PixelRect Fit(AnomalyTrainingImage image, AnomalyTrainingModel model, PixelRect bounds)
    {
        if (model.Kind == EAnomalyTrainingKind.FixedContent && model.Width != null)
        {
            if (model.Width > image.Image.Width || model.Height > image.Image.Height)
            {
                throw new ArgumentException("模型尺寸大于图像。");
            }

            var box = Centered(bounds, model.Width.Value, model.Height!.Value);
            return new PixelRect(
                Math.Min(Math.Max(0, box.X), image.Image.Width - box.Width),
                Math.Min(Math.Max(0, box.Y), image.Image.Height - box.Height),
                box.Width,
                box.Height
            );
        }

        return Inside(image, bounds);
    }

    private static PixelRect Inside(AnomalyTrainingImage image, PixelRect bounds)
    {
        if (!bounds.Fits(image.Image) || bounds.Width < 8 || bounds.Height < 8)
        {
            throw new ArgumentException("框须在图像内且至少8×8像素。");
        }

        return bounds;
    }

    private static PixelRect Centered(PixelRect bounds, int width, int height)
    {
        return new PixelRect(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height
        );
    }

    /// <summary>模型全部样本改为新尺寸（保持各自中心并移入图内）。</summary>
    private void Resize(AnomalyTrainingModel model, int width, int height)
    {
        model.Width = width;
        model.Height = height;
        foreach (var s in Of(model))
        {
            s.Bounds = Fit(s.Image, model, s.Bounds);
        }
    }

    /// <summary>在±SnapRadius内寻找与模型第一个样本归一化相关最高的位置。</summary>
    private PixelRect Snap(AnomalyTrainingImage image, AnomalyTrainingModel model, PixelRect box)
    {
        var reference = _samples.FirstOrDefault(s => s.Model == model);
        if (reference == null || SnapRadius <= 0)
        {
            return box;
        }

        var template = Gray(reference.Image.Image);
        var target = Gray(image.Image);
        int tw = reference.Image.Image.Width,
            iw = image.Image.Width,
            ih = image.Image.Height,
            w = box.Width,
            h = box.Height,
            step = (long)w * h > 40000 ? 2 : 1;
        double tMean = 0,
            tVar = 0;
        int n = 0;
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                tMean += template[(reference.Bounds.Y + y) * tw + reference.Bounds.X + x];
                n++;
            }
        }

        tMean /= n;
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                double t = template[(reference.Bounds.Y + y) * tw + reference.Bounds.X + x] - tMean;
                tVar += t * t;
            }
        }

        if (tVar <= 0)
        {
            return box;
        }

        double best = double.MinValue;
        var result = box;
        int r = Math.Min(64, SnapRadius);
        for (int dy = -r; dy <= r; dy++)
        {
            int oy = box.Y + dy;
            if (oy < 0 || oy + h > ih)
            {
                continue;
            }

            for (int dx = -r; dx <= r; dx++)
            {
                int ox = box.X + dx;
                if (ox < 0 || ox + w > iw)
                {
                    continue;
                }

                double sum = 0,
                    sumSq = 0,
                    cross = 0;
                for (int y = 0; y < h; y += step)
                {
                    int trow = (reference.Bounds.Y + y) * tw + reference.Bounds.X,
                        irow = (oy + y) * iw + ox;
                    for (int x = 0; x < w; x += step)
                    {
                        double v = target[irow + x];
                        sum += v;
                        sumSq += v * v;
                        cross += v * (template[trow + x] - tMean);
                    }
                }

                double variance = sumSq - sum * sum / n;
                if (variance <= 0)
                {
                    continue;
                }

                double score = cross / Math.Sqrt(variance * tVar);
                // 同分时取离手画位置最近的，避免均匀区域漂移。
                if (
                    score > best + 1e-9
                    || Math.Abs(score - best) <= 1e-9
                        && Math.Abs(dx) + Math.Abs(dy)
                            < Math.Abs(result.X - box.X) + Math.Abs(result.Y - box.Y)
                )
                {
                    best = score;
                    result = new PixelRect(ox, oy, w, h);
                }
            }
        }

        return result;
    }

    private byte[] Gray(ImageFrame frame)
    {
        if (_gray.TryGetValue(frame, out var cached))
        {
            return cached;
        }

        var pixels = frame.CopyPixels();
        byte[] gray;
        if (frame.Format == EImagePixelFormat.Gray8)
        {
            gray = pixels;
        }
        else
        {
            gray = new byte[frame.Width * frame.Height];
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(
                    (pixels[i * 3] * 29 + pixels[i * 3 + 1] * 150 + pixels[i * 3 + 2] * 77) >> 8
                );
            }
        }

        return _gray[frame] = gray;
    }
}
