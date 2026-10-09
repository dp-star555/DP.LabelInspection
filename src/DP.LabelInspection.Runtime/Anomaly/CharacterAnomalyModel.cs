using System;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;

namespace DP.LabelInspection.Runtime;

/// <summary>字符模型的借用运行实例与制作几何；原生实例由缓存租约拥有。</summary>
public sealed class CharacterAnomalyModel
{
    /// <summary>兼容已有Patch调用，业务执行统一走运行实例。</summary>
    /// <param name="entry">固定修订条目。</param>
    /// <param name="model">旧模型。</param>
    /// <param name="detector">借用旧算法。</param>
    public CharacterAnomalyModel(AnomalyModelEntry entry, PatchAnomalyModel model, IPatchAnomalyDetector detector)
        : this(entry, new PatchAnomalyImplementation(model.FeatureSource, detector).Load(PatchAnomalyImplementation.Capture(model, entry.Width, entry.Height))) { }
    /// <summary>组合任意厂商的字符运行实例。</summary>
    /// <param name="entry">固定修订字符条目。</param>
    /// <param name="runtime">借用已加载实例。</param>
    public CharacterAnomalyModel(AnomalyModelEntry entry, ILoadedAnomalyModel runtime)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (entry.Scope != EAnomalyModelScope.Character || entry.FeatureSource != runtime.Asset.ImplementationId)
            throw new ArgumentException("字符模型范围/实现身份不匹配。", nameof(entry));
        Reference = new CharacterAnomalyReference(runtime, new AnomalyDetectionOptions(entry.Threshold, entry.MinimumArea),
            entry.Width, entry.Height, entry.InkThreshold, entry.Normalization);
    }
    internal CharacterAnomalyReference Reference { get; }
    /// <summary>固定版本条目。</summary>
    public AnomalyModelEntry Entry { get; }
    /// <summary>借用厂商运行实例。</summary>
    public ILoadedAnomalyModel Runtime { get; }
    /// <summary>旧Patch调用兼容；原生模型不得当Patch解码。</summary>
    public PatchAnomalyModel Model => Runtime is PatchAnomalyRuntime patch ? patch.Model : throw new NotSupportedException("本模型不是Patch记忆库。");
    /// <summary>旧Patch算法兼容。</summary>
    public IPatchAnomalyDetector Detector => Runtime is PatchAnomalyRuntime patch ? patch.Detector : throw new NotSupportedException("本模型使用原生运行实现。");
}
