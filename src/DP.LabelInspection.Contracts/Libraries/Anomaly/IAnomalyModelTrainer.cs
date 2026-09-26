using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>由宿主注入的异常模型训练能力（方法B），使界面不依赖具体视觉库。</summary>
public interface IAnomalyModelTrainer
{
    /// <summary>用已与配方对齐的整张良品图训练一个ROI的模型，返回可发布到模型库的条目。</summary>
    /// <param name = "good">已与配方对齐的整张良品图，至少1张；建议覆盖正常波动的多张。</param>
    /// <param name = "region">要训练的ROI（配方坐标），按其类型和内容选择默认模式。</param>
    /// <param name = "key">模型键；null时使用ROI名称。</param>
    /// <param name = "token">协作式取消标记。</param>
    AnomalyModelEntry Train(
        IReadOnlyList<ImageFrame> good,
        InspectionRegion region,
        string? key = null,
        CancellationToken token = default
    );

    /// <summary>
    /// 用逐个框出的样本训练整ROI模型（批量训练页）：每个样本自带框，可来自不同图、不同位置。
    /// 位置相关模式要求各样本框尺寸相同（与位置无关模式不要求）。
    /// </summary>
    /// <param name = "samples">良品样本，至少1个。</param>
    /// <param name = "region">检测时使用的ROI（类型、名称；范围取第一个样本框的尺寸）。</param>
    /// <param name = "positionDependent">true为位置相关（内容固定），false为与位置无关（内容可变）。</param>
    /// <param name = "key">模型键；null时使用ROI名称。</param>
    /// <param name = "token">协作式取消标记。</param>
    AnomalyModelEntry TrainSamples(
        IReadOnlyList<RegionAnomalySample> samples,
        InspectionRegion region,
        bool positionDependent,
        string? key = null,
        CancellationToken token = default
    );

    /// <summary>
    /// 训练逐字符模型：按字符身份汇总所有行中的样本，每个字符一个模型（位置相关，按行高归一化），
    /// 返回<see cref = "EAnomalyModelScope.Character"/>条目。只含非字母数字的字符不训练。
    /// </summary>
    /// <param name = "lines">良品行样本，字符身份须已确认。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<AnomalyModelEntry> TrainCharacters(
        IReadOnlyList<CharacterAnomalySample> lines,
        CancellationToken token = default
    );
}
