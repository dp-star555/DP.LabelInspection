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
}
