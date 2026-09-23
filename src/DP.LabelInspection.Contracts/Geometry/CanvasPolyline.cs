using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>不可变的有序轮廓，明确区分开放与闭合拓扑。</summary>
public sealed class CanvasPolyline
{
    /// <summary>复制轮廓点；几何保留双精度，渲染器可使用单精度屏幕坐标。</summary>
    /// <param name = "id">源轮廓标识。</param>
    /// <param name = "points">有序双精度像素中心坐标集合，内部复制。</param>
    /// <param name = "closed">是否连接末点与首点，不隐式填充区域。</param>
    /// <param name = "strokeArgb">轮廓线ARGB显示颜色。</param>
    public CanvasPolyline(
        string id,
        IEnumerable<CanvasPoint> points,
        bool closed,
        uint strokeArgb = 0xFFFF3388
    )
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Geometry ID required.");
        }

        var copy = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copy.Length > 2000000)
        {
            throw new ArgumentException("Too many contour points.");
        }

        Id = id;
        Points = Array.AsReadOnly(copy);
        Closed = closed;
        StrokeArgb = strokeArgb;
    }

    /// <summary>快照中的源轮廓标识。</summary>
    public string Id { get; }

    /// <summary>本对象拥有的有序亚像素坐标。</summary>
    public IReadOnlyList<CanvasPoint> Points { get; }

    /// <summary>末点是否连接首点。</summary>
    public bool Closed { get; }

    /// <summary>打包ARGB显示颜色。</summary>
    public uint StrokeArgb { get; }
}
