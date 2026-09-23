using System;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

internal static class CvImages
{
    internal static Mat Mat(ImageFrame frame)
    {
        var output = new Mat(
            frame.Height,
            frame.Width,
            frame.Format == EImagePixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC3
        );
        try
        {
            var bytes = frame.CopyPixels();
            Marshal.Copy(bytes, 0, output.Data, bytes.Length);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    internal static ImageFrame Frame(Mat image)
    {
        if (image.Channels() != 1 && image.Channels() != 3)
        {
            throw new ArgumentException("Gray8/BGR8 required.");
        }

        using var packed = image.Clone();
        var bytes = new byte[checked(packed.Rows * packed.Cols * packed.Channels())];
        Marshal.Copy(packed.Data, bytes, 0, bytes.Length);
        return new ImageFrame(
            packed.Cols,
            packed.Rows,
            packed.Channels() == 1 ? EImagePixelFormat.Gray8 : EImagePixelFormat.Bgr24,
            bytes
        );
    }

    internal static Rect Rect(PixelRect r)
    {
        return new Rect(r.X, r.Y, r.Width, r.Height);
    }
}
