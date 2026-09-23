using System;
using DP.Vision.Algorithms;

namespace DP.LabelInspection.Runtime;

/// <summary>宿主选择的独立固定及空白测量；生命周期由宿主拥有，后台调用由引擎串行化。</summary>
public sealed class RegionQualityAlgorithms
{
    /// <summary>提供两项所需测量，不向任一算法引入标签契约。</summary>
    /// <param name = "fixedQuality">宿主拥有的固定区域质量实现。</param>
    /// <param name = "blankQuality">宿主拥有的空白区域质量实现。</param>
    public RegionQualityAlgorithms(IFixedQualityInspector fixedQuality, IBlankQualityInspector blankQuality)
    {
        Fixed = fixedQuality ?? throw new ArgumentNullException(nameof(fixedQuality));
        Blank = blankQuality ?? throw new ArgumentNullException(nameof(blankQuality));
    }

    /// <summary>已对齐的固定区域测量。</summary>
    public IFixedQualityInspector Fixed { get; }

    /// <summary>空白区域测量。</summary>
    public IBlankQualityInspector Blank { get; }
}
