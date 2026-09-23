using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>保留孔洞和分离部分的不可变像素区域，不含厂商对象句柄。</summary>
public sealed class CanvasRegion
{
    /// <summary>复制已排序且不重叠的游程；空区域仍作为具有零条游程的对象保留。</summary>
    /// <param name = "id">源区域对象标识。</param>
    /// <param name = "runs">按行及列排序、互不重叠的游程集合，内部复制。</param>
    /// <param name = "fillArgb">填充ARGB颜色，最高字节为Alpha，不影响区域像素成员关系。</param>
    public CanvasRegion(string id, IEnumerable<CanvasRun> runs, uint fillArgb = 0x7033CC66)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Geometry ID required.");
        }

        var copy = runs?.ToArray() ?? throw new ArgumentNullException(nameof(runs));
        if (copy.Length > 2000000)
        {
            throw new ArgumentException("Too many runs.");
        }

        for (int i = 0; i < copy.Length; i++)
        {
            var r = copy[i];
            if (r.EndColumnExclusive <= r.StartColumn)
            {
                throw new ArgumentException("Invalid run.");
            }

            if (
                i > 0
                && (
                    r.Row < copy[i - 1].Row
                    || (r.Row == copy[i - 1].Row && r.StartColumn < copy[i - 1].EndColumnExclusive)
                )
            )
            {
                throw new ArgumentException("Runs must be sorted and nonoverlapping.");
            }
        }

        Id = id;
        Runs = Array.AsReadOnly(copy);
        FillArgb = fillArgb;
    }

    /// <summary>快照中的源对象标识。</summary>
    public string Id { get; }

    /// <summary>本对象拥有的原图像素游程。</summary>
    public IReadOnlyList<CanvasRun> Runs { get; }

    /// <summary>打包ARGB显示颜色，不依赖System.Drawing。</summary>
    public uint FillArgb { get; }

    /// <summary>精确的区域像素面积，不受屏幕缩放影响。</summary>
    public long AreaPixels => Runs.Sum(r => (long)r.EndColumnExclusive - r.StartColumn);

    /// <summary>不借助厂商运行时测试原图像素成员关系。</summary>
    /// <param name = "column">要测试的原图像素列。</param>
    /// <param name = "row">要测试的原图像素行。</param>
    public bool Contains(int column, int row)
    {
        return Runs.Any(r => r.Row == row && column >= r.StartColumn && column < r.EndColumnExclusive);
    }
}
