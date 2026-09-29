using System;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.OnnxDetection;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

/// <summary>把原图准备成DB检测模型输入：缩放、BGR、均值方差归一化，并保留原图几何映射。</summary>
/// <remarks>只做模型输入准备，不做推理，也不产生候选框；OpenCV只在业务侧使用。</remarks>
public static class PPOcrDetectionPreparer
{
    /// <summary>PP-OCRv4 DB检测的最长边目标尺寸。</summary>
    public const double LongestSide = 736.0;

    /// <summary>模型输入边长的对齐粒度。</summary>
    public const int Alignment = 32;

    /// <summary>把Gray8或Bgr24原图准备成连续NCHW输入。</summary>
    /// <param name = "frame">借用的不可变原图，返回前租约须有效。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>模型输入张量及原图几何映射。</returns>
    public static PPOcrDetectionInput Prepare(DP.Vision.IImageSource frame, CancellationToken token = default)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        token.ThrowIfCancellationRequested();
        if (
            frame.Info.Layout != DP.Vision.EPixelLayout.Gray8
            && frame.Info.Layout != DP.Vision.EPixelLayout.Bgr24
        )
        {
            throw new NotSupportedException("DB detector requires Gray8 or Bgr24.");
        }

        int imageWidth = frame.Info.Width,
            imageHeight = frame.Info.Height,
            channels = frame.Info.Layout == DP.Vision.EPixelLayout.Gray8 ? 1 : 3;
        double ratio = Math.Min(1, LongestSide / Math.Max(imageWidth, imageHeight));
        int width = Math.Max(
                Alignment,
                (int)Math.Round(imageWidth * ratio / Alignment) * Alignment
            ),
            height = Math.Max(
                Alignment,
                (int)Math.Round(imageHeight * ratio / Alignment) * Alignment
            );
        using var raw = new Mat(
            imageHeight,
            imageWidth,
            channels == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3
        );
        var pixels = new byte[frame.Info.ByteLength];
        frame.CopyTo(0, pixels, 0, pixels.Length);
        for (int row = 0; row < imageHeight; row++)
        {
            Marshal.Copy(pixels, row * frame.Info.Stride, raw.Ptr(row), imageWidth * channels);
        }

        using var bgr = new Mat();
        if (raw.Channels() == 1)
        {
            Cv2.CvtColor(raw, bgr, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            raw.CopyTo(bgr);
        }

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(width, height));
        var source = new byte[width * height * 3];
        Marshal.Copy(resized.Data, source, 0, source.Length);
        var values = new float[width * height * 3];
        float[] mean =  { .485f, .456f, .406f },
            std =  { .229f, .224f, .225f };
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                values[c * width * height + i] = (source[3 * i + c] / 255f - mean[c]) / std[c];
            }
        }

        token.ThrowIfCancellationRequested();
        return new PPOcrDetectionInput(width, height, imageWidth, imageHeight, values);
    }
}
