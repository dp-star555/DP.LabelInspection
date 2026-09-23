using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>可替换的条码解码器。</summary>
public interface IBarcodeDecoder
{
    /// <summary>从选定ROI解码码符号。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "bounds">原图中需要解码的整数范围。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<BarcodeObservation> Decode(ImageFrame frame, PixelRect bounds, CancellationToken token);
}
