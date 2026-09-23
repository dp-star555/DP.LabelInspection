using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>检测完成通知。</summary>
public sealed class InspectionCompletedEventArgs : EventArgs
{
    /// <summary>创建通知。</summary>
    /// <param name = "report">不可变的完整报告。</param>
    public InspectionCompletedEventArgs(InspectionReport report)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>完整检测结果。</summary>
    public InspectionReport Report { get; }
}
