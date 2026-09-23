using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

// 冻结的迁移前整图BGR/GDI基线，不得替换为已迁移工作台的委托入口。
namespace DP.LabelInspection.LegacyBenchmark;

/// <summary>原图坐标中的新ROI。</summary>
public sealed class RegionDrawnEventArgs : EventArgs
{
    /// <summary>创建事件数据。</summary>
    /// <param name = "bounds">原图像素区域。</param>
    public RegionDrawnEventArgs(PixelRect bounds) => Bounds = bounds;

    /// <summary>原图坐标范围。</summary>
    public PixelRect Bounds { get; }
}
