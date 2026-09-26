using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>
/// 批量训练页中的一个样本框。逐字符模型的框在提取后带有分割出的字符、可修改的身份及是否作为样本；
/// 框变化后需重新提取。
/// </summary>
public sealed class AnomalyTrainingSample
{
    internal AnomalyTrainingSample(AnomalyTrainingImage image, AnomalyTrainingModel model, PixelRect bounds)
    {
        Image = image;
        Model = model;
        Bounds = bounds;
    }

    /// <summary>所在图像。</summary>
    public AnomalyTrainingImage Image { get; }

    /// <summary>所属模型。</summary>
    public AnomalyTrainingModel Model { get; }

    /// <summary>样本框（原图坐标）。</summary>
    public PixelRect Bounds { get; internal set; }

    /// <summary>逐字符模型：人工确认的整行文本（提取时代替OCR）；null表示使用OCR读数。</summary>
    public string? ConfirmedText { get; internal set; }

    /// <summary>逐字符模型的分割结果；未提取时为null。</summary>
    public CharacterSegmentation? Segmentation { get; internal set; }

    /// <summary>逐字符模型：各字符的身份（可人工修改）。</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>逐字符模型：各字符是否作为训练样本。</summary>
    public IReadOnlyList<bool> Include => _include;

    /// <summary>逐字符模型：提取失败或需复核的原因；正常时为null。</summary>
    public string? Problem { get; internal set; }

    /// <summary>逐字符模型是否还需要提取。</summary>
    public bool NeedsExtraction =>
        Model.Kind == EAnomalyTrainingKind.Characters && Segmentation == null && Problem == null;

    internal string[] _labels = Array.Empty<string>();
    internal bool[] _include = Array.Empty<bool>();

    internal void ClearExtraction()
    {
        Segmentation = null;
        Problem = null;
        _labels = Array.Empty<string>();
        _include = Array.Empty<bool>();
    }

    internal CharacterAnomalySample ToCharacterSample()
    {
        var characters = Segmentation!.Characters.Select(
            (c, i) =>
                _labels[i] == c.Character
                    ? c
                    : new CharacterPatch(_labels[i], c.TokenIndex, c.Bounds, c.Patch, c.NeighborInkRemoved)
        );
        return new CharacterAnomalySample(
            Image.Image,
            characters,
            Enumerable.Range(0, _include.Length).Where(i => !_include[i]),
            Image.Name + " / " + Model.Name
        );
    }
}
