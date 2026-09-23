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

/// <summary>显示图像坐标系中的一次已提交编辑。</summary>
public sealed class RegionEditedEventArgs : EventArgs
{
    /// <summary>创建从0开始编号的ROI编辑事件。</summary>
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
