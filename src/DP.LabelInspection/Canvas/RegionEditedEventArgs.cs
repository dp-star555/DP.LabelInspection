using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>显示图像坐标系中的一次已提交编辑。</summary>
public sealed class RegionEditedEventArgs : EventArgs
{
    /// <summary>创建从0开始编号的ROI编辑事件。</summary>
    /// <param name = "index">当前ROI叠加列表中从0开始的索引。</param>
    /// <param name = "bounds">编辑后的原图像素范围。</param>
    public RegionEditedEventArgs(int index, PixelRect bounds)
    {
        Index = index;
        Bounds = bounds;
    }

    /// <summary>在当前配方叠加列表中的索引。</summary>
    public int Index { get; }

    /// <summary>新的显示图像范围。</summary>
    public PixelRect Bounds { get; }
}
