using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>条码解码观测；解码成功不等于ISO印刷等级通过。</summary>
public sealed class BarcodeObservation
{
    /// <summary>创建解码观测。</summary>
    /// <param name = "text">原始解码内容，不用预期值修正。</param>
    /// <param name = "format">解码得到的码制名称。</param>
    /// <param name = "bounds">选定的原图像素范围。</param>
    /// <param name = "moduleGrid">可选的实测QR网格，不能作为独立标准真值。</param>
    public BarcodeObservation(
        string text,
        string format,
        PixelRect bounds,
        BarcodeModuleGrid? moduleGrid = null
    )
    {
        Text = text;
        Format = format;
        Bounds = bounds;
        ModuleGrid = moduleGrid;
    }

    /// <summary>可选的实测QR几何，与内容是否正确无关。</summary>
    public BarcodeModuleGrid? ModuleGrid { get; }

    /// <summary>解码内容。</summary>
    public string Text { get; }

    /// <summary>码符号类别。</summary>
    public string Format { get; }

    /// <summary>原图中选定的ROI，不是经认证的码轮廓。</summary>
    public PixelRect Bounds { get; }
}
