using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>保守的水平文本区域候选任务，候选不代表检测覆盖认证。</summary>
public interface ITextRegionDetector : System.IDisposable
{
    /// <summary>实际加载检测器的标识或哈希。</summary>
    string ModelIdentity { get; }

    /// <summary>从真实推理返回原图坐标的水平候选行。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<PixelRect> Detect(ImageFrame frame, CancellationToken token);
}
