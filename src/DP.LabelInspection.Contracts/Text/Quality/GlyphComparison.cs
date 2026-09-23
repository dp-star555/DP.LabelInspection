using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>归一化单字比较，包含实际图、参考图和差异图。</summary>
public sealed class GlyphComparison
{
    /// <summary>创建归一化测量结果。</summary>
    /// <param name = "status">比较完成或空参考等状态码。</param>
    /// <param name = "difference">归一化缺墨加多墨除以参考墨迹的比例。</param>
    /// <param name = "missing">缺墨的归一化像素数。</param>
    /// <param name = "extra">多墨的归一化像素数。</param>
    /// <param name = "actual">对齐后的归一化实际图。</param>
    /// <param name = "reference">归一化参考图。</param>
    /// <param name = "delta">经容差过滤的彩色差异图，不是原图直接差分。</param>
    public GlyphComparison(
        string status,
        double difference,
        int missing,
        int extra,
        ImageFrame actual,
        ImageFrame reference,
        ImageFrame delta
    )
    {
        Status = status;
        Difference = difference;
        Missing = missing;
        Extra = extra;
        Actual = actual;
        Reference = reference;
        Delta = delta;
    }

    /// <summary>状态码：compared已比较或empty_reference空参考。</summary>
    public string Status { get; }

    /// <summary>归一化差异比，不是毫米或原图像素误差。</summary>
    public double Difference { get; }

    /// <summary>缺墨的归一化像素数。</summary>
    public int Missing { get; }

    /// <summary>多墨的归一化像素数。</summary>
    public int Extra { get; }

    /// <summary>已对齐的归一化实际图。</summary>
    public ImageFrame Actual { get; }

    /// <summary>归一化参考图。</summary>
    public ImageFrame Reference { get; }

    /// <summary>彩色差异图。</summary>
    public ImageFrame Delta { get; }
}
