using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>可移植印刷检查接口；必须声明不支持的几何，不能将解码成功视为印刷合格。</summary>
public interface IBarcodePrintInspector
{
    /// <summary>检查原始像素并返回原图坐标证据；观测不是重新生成的标准码像素。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "bounds">原图中的整数检查范围。</param>
    /// <param name = "symbols">实际解码观测，可用于结构，不作为纠错后的印刷真值。</param>
    /// <param name = "options">局部印刷阈值及开关。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<InspectionFinding> Inspect(
        ImageFrame frame,
        PixelRect bounds,
        IReadOnlyList<BarcodeObservation> symbols,
        BarcodePrintOptions options,
        CancellationToken token
    );
}
