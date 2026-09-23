using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>仅供WinForms使用的位图转换；契约和引擎不依赖System.Drawing。</summary>
public static class DrawingImageConverter
{
    /// <summary>将图像合成到白色背景，生成独立BGR24快照。</summary>
    /// <param name = "image">借用的源图，不由本方法释放。</param>
    /// <returns>不可变图像快照。</returns>
    public static ImageFrame FromImage(Image image)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        if (image.Width > 12000 || image.Height > 12000 || (long)image.Width * image.Height > 16000000)
        {
            throw new ArgumentException("Image too large.", nameof(image));
        }

        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            // DrawImageUnscaled可能应用源DPI，使高DPI文件在目标范围内缩小。
            // 显式指定源和目标像素矩形以保留原始像素网格；透明度合成到白色背景。
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(
                image,
                new Rectangle(0, 0, image.Width, image.Height),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel
            );
        }

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb
        );
        try
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    pixels,
                    y * bitmap.Width * 3,
                    bitmap.Width * 3
                );
            }

            return new ImageFrame(bitmap.Width, bitmap.Height, EImagePixelFormat.Bgr24, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>创建新的GDI位图，调用方拥有并负责释放。</summary>
    /// <param name = "frame">不可变源图。</param>
    /// <returns>调用方拥有的位图。</returns>
    public static Bitmap ToBitmap(ImageFrame frame)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format24bppRgb);
        try
        {
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb
            );
            try
            {
                var source = frame.CopyPixels();
                var row = new byte[frame.Width * 3];
                for (int y = 0; y < frame.Height; y++)
                {
                    if (frame.Format == EImagePixelFormat.Bgr24)
                    {
                        Buffer.BlockCopy(source, y * frame.Stride, row, 0, row.Length);
                    }
                    else
                    {
                        for (int x = 0; x < frame.Width; x++)
                        {
                            row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = source[y * frame.Stride + x];
                        }
                    }

                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
