using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>厂商中立的几何快照，仅有几何不代表保留XLD属性。</summary>
public sealed class CanvasGeometry
{
    /// <summary>复制对象集合，其中各对象不可变。</summary>
    /// <param name = "regions">独立区域集合，保留空对象。</param>
    /// <param name = "contours">独立有序轮廓集合，保留各自闭合状态。</param>
    public CanvasGeometry(IEnumerable<CanvasRegion> regions, IEnumerable<CanvasPolyline> contours)
    {
        var r = regions?.ToArray() ?? throw new ArgumentNullException(nameof(regions));
        var c = contours?.ToArray() ?? throw new ArgumentNullException(nameof(contours));
        if (
            r.Any(x => x == null)
            || c.Any(x => x == null)
            || r.Length + c.Length > 10000
            || r.Sum(x => (long)x.Runs.Count) + c.Sum(x => (long)x.Points.Count) > 2000000
        )
        {
            throw new ArgumentException("Invalid or oversized geometry snapshot.");
        }

        Regions = Array.AsReadOnly(r);
        Contours = Array.AsReadOnly(c);
    }

    /// <summary>独立区域对象，包含空对象。</summary>
    public IReadOnlyList<CanvasRegion> Regions { get; }

    /// <summary>独立的开放或闭合轮廓。</summary>
    public IReadOnlyList<CanvasPolyline> Contours { get; }
}
