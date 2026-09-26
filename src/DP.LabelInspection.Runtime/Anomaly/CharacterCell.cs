using System;
using DP.LabelInspection.Contracts;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

/// <summary>归一化的字符单元及其到原图的映射：原图坐标 = X0 + 单元坐标 / Scale。</summary>
internal sealed class CharacterCell : IDisposable
{
    internal CharacterCell(Mat image, double x0, double y0, double scale)
    {
        Image = image;
        X0 = x0;
        Y0 = y0;
        Scale = scale;
    }

    /// <summary>归一化灰度单元，由本对象拥有。</summary>
    internal Mat Image { get; }

    /// <summary>单元左上角的原图横坐标。</summary>
    internal double X0 { get; }

    /// <summary>单元左上角的原图纵坐标。</summary>
    internal double Y0 { get; }

    /// <summary>原图到单元的缩放倍数。</summary>
    internal double Scale { get; }

    /// <summary>单元坐标矩形换算为原图矩形（向外取整）。</summary>
    /// <param name = "x">单元横坐标。</param>
    /// <param name = "y">单元纵坐标。</param>
    /// <param name = "width">单元宽度。</param>
    /// <param name = "height">单元高度。</param>
    internal PixelRect ToImage(int x, int y, int width, int height)
    {
        int left = (int)Math.Floor(X0 + x / Scale),
            top = (int)Math.Floor(Y0 + y / Scale),
            right = (int)Math.Ceiling(X0 + (x + width) / Scale),
            bottom = (int)Math.Ceiling(Y0 + (y + height) / Scale);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public void Dispose()
    {
        Image.Dispose();
    }
}
