using System;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;

namespace DP.LabelInspection.Runtime;

/// <summary>可直接检测的字符模型：库条目、解析后的模型及与其特征来源匹配的检测实现。</summary>
public sealed class CharacterAnomalyModel
{
    /// <summary>组合字符模型。</summary>
    /// <param name = "entry">字符范围的库条目。</param>
    /// <param name = "model">由条目字节解析的模型。</param>
    /// <param name = "detector">与模型特征来源一致的检测实现，调用方拥有。</param>
    public CharacterAnomalyModel(
        AnomalyModelEntry entry,
        PatchAnomalyModel model,
        IPatchAnomalyDetector detector
    )
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Detector = detector ?? throw new ArgumentNullException(nameof(detector));
        if (entry.Scope != EAnomalyModelScope.Character)
        {
            throw new ArgumentException("A character-scope entry is required.", nameof(entry));
        }
    }

    /// <summary>缺墨检查的良品参考（按模型对象缓存，线程安全）；未标定缺墨阈值或模型不支持时为null。</summary>
    internal CharacterInkLoss? Ink =>
        Entry.InkThreshold != null && CharacterInkLoss.Supports(Model) ? CharacterInkLoss.For(Model) : null;

    /// <summary>库条目。</summary>
    public AnomalyModelEntry Entry { get; }

    /// <summary>解析后的模型。</summary>
    public PatchAnomalyModel Model { get; }

    /// <summary>检测实现。</summary>
    public IPatchAnomalyDetector Detector { get; }
}
