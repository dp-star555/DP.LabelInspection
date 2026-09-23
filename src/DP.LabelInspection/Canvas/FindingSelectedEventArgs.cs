using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>选中的检测证据索引。</summary>
public sealed class FindingSelectedEventArgs : EventArgs
{
    /// <summary>创建从0开始编号的证据选择事件。</summary>
    /// <param name = "index">当前发现列表中从0开始的证据索引。</param>
    public FindingSelectedEventArgs(int index)
    {
        Index = index;
    }

    /// <summary>在当前发现列表中的索引。</summary>
    public int Index { get; }
}
