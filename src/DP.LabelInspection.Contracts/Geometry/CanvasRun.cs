using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>右端列排他的像素游程；负坐标或图像外几何保留到渲染阶段处理。</summary>
public readonly struct CanvasRun
{
    /// <summary>创建原图坐标中的非空像素游程。</summary>
    /// <param name = "row">原图像素行。</param>
    /// <param name = "startColumn">首个包含的像素列。</param>
    /// <param name = "endColumnExclusive">首个不包含的像素列，必须大于起始列。</param>
    public CanvasRun(int row, int startColumn, int endColumnExclusive)
    {
        if (
            Math.Abs((long)row) > 1000000
            || Math.Abs((long)startColumn) > 1000000
            || Math.Abs((long)endColumnExclusive) > 1000000
            || endColumnExclusive <= startColumn
        )
        {
            throw new ArgumentException("Invalid canvas run.");
        }

        Row = row;
        StartColumn = startColumn;
        EndColumnExclusive = endColumnExclusive;
    }

    /// <summary>原图像素行。</summary>
    public int Row { get; }

    /// <summary>第一个包含的像素列。</summary>
    public int StartColumn { get; }

    /// <summary>第一个不包含的像素列。</summary>
    public int EndColumnExclusive { get; }
}
