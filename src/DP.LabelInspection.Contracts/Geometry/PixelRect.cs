using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>原图坐标中的整数半开矩形。</summary>
public readonly struct PixelRect
{
    /// <summary>创建非空矩形。</summary>
    /// <param name = "x">左侧坐标。</param>
    /// <param name = "y">顶部坐标。</param>
    /// <param name = "width">正数宽度。</param>
    /// <param name = "height">正数高度。</param>
    public PixelRect(int x, int y, int width, int height)
    {
        if (
            x < 0
            || y < 0
            || width < 1
            || height < 1
            || (long)x + width > int.MaxValue
            || (long)y + height > int.MaxValue
        )
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>左侧坐标。</summary>
    public int X { get; }

    /// <summary>顶部坐标。</summary>
    public int Y { get; }

    /// <summary>宽度。</summary>
    public int Width { get; }

    /// <summary>高度。</summary>
    public int Height { get; }

    /// <summary>检查包含关系，并拒绝默认空矩形。</summary>
    /// <param name = "frame">用于确定边界的图像。</param>
    /// <returns>全部像素是否位于图像内。</returns>
    public bool Fits(ImageFrame frame)
    {
        return Width > 0 && Height > 0 && (long)X + Width <= frame.Width && (long)Y + Height <= frame.Height;
    }

    /// <summary>检查是否存在非空交集。</summary>
    /// <param name = "other">另一块区域。</param>
    /// <returns>存在重叠像素时为true。</returns>
    public bool Intersects(PixelRect other)
    {
        return X < (long)other.X + other.Width
            && other.X < (long)X + Width
            && Y < (long)other.Y + other.Height
            && other.Y < (long)Y + Height;
    }

    /// <summary>返回显示用坐标。</summary>
    /// <returns>按X、Y、宽度、高度排列的文本。</returns>
    public override string ToString()
    {
        return $"[{X},{Y},{Width},{Height}]";
    }
}
