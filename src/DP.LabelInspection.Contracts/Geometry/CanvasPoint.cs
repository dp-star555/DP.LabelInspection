using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>亚像素图像坐标；整数X/Y表示像素中心，不是像素单元边缘。</summary>
public readonly struct CanvasPoint
{
    /// <summary>创建有限且范围受限的亚像素点。</summary>
    /// <param name = "x">双精度列坐标，整数表示像素中心。</param>
    /// <param name = "y">双精度行坐标，整数表示像素中心。</param>
    public CanvasPoint(double x, double y)
    {
        if (
            double.IsNaN(x)
            || double.IsNaN(y)
            || double.IsInfinity(x)
            || double.IsInfinity(y)
            || Math.Abs(x) > 1000000
            || Math.Abs(y) > 1000000
        )
        {
            throw new ArgumentException("Invalid subpixel point.");
        }

        X = x;
        Y = y;
    }

    /// <summary>保留双精度的列坐标。</summary>
    public double X { get; }

    /// <summary>保留双精度的行坐标。</summary>
    public double Y { get; }
}
