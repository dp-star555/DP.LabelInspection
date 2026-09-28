using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Wpf;

/// <summary>供宿主存储使用的WPF通知，包含固定输入与输出。</summary>
public sealed class WpfInspectionCompletedEventArgs : EventArgs
{
    /// <summary>创建检测完成通知。</summary>
    /// <param name = "request">借用本轮Vision帧请求，供同步存储；下次检测或控件释放后失效。</param>
    /// <param name = "report">本次完整检测报告，与request对应同一轮。</param>
    public WpfInspectionCompletedEventArgs(InspectionRequest request, InspectionReport report)
    {
        Request = request;
        Report = report;
    }

    /// <summary>借用输入；需在事件处理期间完成存储，不得缓存本对象供后续异步访问。</summary>
    public InspectionRequest Request { get; }

    /// <summary>完整报告。</summary>
    public InspectionReport Report { get; }
}
