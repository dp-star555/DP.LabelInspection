using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>独立拥有像素的不可变图像快照；构造和导出均复制缓冲区，调用方仍拥有其输入。</summary>
public sealed class ImageFrame
{
    private readonly byte[] _pixels;

    /// <summary>构造从上到下、紧密排列的图像。</summary>
    /// <param name = "width">图像宽度，单位为像素，最大12000。</param>
    /// <param name = "height">图像高度，单位为像素，最大12000；总像素数不超过1600万。</param>
    /// <param name = "format">像素布局：Gray8灰度或Bgr24彩色。</param>
    /// <param name = "pixels">复制到快照的缓冲区，长度必须等于行跨度乘高度。</param>
    public ImageFrame(int width, int height, EImagePixelFormat format, byte[] pixels)
    {
        if (width < 1 || height < 1 || width > 12000 || height > 12000 || (long)width * height > 16000000)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid image dimensions.");
        }

        if (!Enum.IsDefined(typeof(EImagePixelFormat), format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (pixels == null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        Width = width;
        Height = height;
        Format = format;
        if (pixels.Length != Stride * height)
        {
            throw new ArgumentException("Expected tightly packed pixels.", nameof(pixels));
        }

        _pixels = (byte[])pixels.Clone();
    }

    /// <summary>原图像素宽度。</summary>
    public int Width { get; }

    /// <summary>原图像素高度。</summary>
    public int Height { get; }

    /// <summary>通道布局。</summary>
    public EImagePixelFormat Format { get; }

    /// <summary>每行字节数，不含填充。</summary>
    public int Stride => Width * (Format == EImagePixelFormat.Gray8 ? 1 : 3);

    /// <summary>返回独立拥有的副本。</summary>
    /// <returns>从上到下排列的像素字节。</returns>
    public byte[] CopyPixels()
    {
        return (byte[])_pixels.Clone();
    }

    /// <summary>将原图坐标裁剪复制为独立快照。</summary>
    /// <param name = "bounds">完全位于图像内的非空裁剪范围。</param>
    /// <returns>独立裁剪像素。</returns>
    public ImageFrame Crop(PixelRect bounds)
    {
        if (!bounds.Fits(this))
        {
            throw new ArgumentException("Crop outside image.", nameof(bounds));
        }

        int channels = Format == EImagePixelFormat.Gray8 ? 1 : 3;
        var bytes = new byte[bounds.Width * bounds.Height * channels];
        for (int y = 0; y < bounds.Height; y++)
        {
            Buffer.BlockCopy(
                _pixels,
                (bounds.Y + y) * Stride + bounds.X * channels,
                bytes,
                y * bounds.Width * channels,
                bounds.Width * channels
            );
        }

        return new ImageFrame(bounds.Width, bounds.Height, Format, bytes);
    }
}
