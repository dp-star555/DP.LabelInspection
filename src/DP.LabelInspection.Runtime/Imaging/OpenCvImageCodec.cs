using System;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

/// <summary>有界托管图像快照和PNG编码，不向外暴露原生图像。</summary>
public sealed class OpenCvImageCodec : IImageCodec
{
    /// <inheritdoc/>
    public ImageFrame Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 64 * 1024 * 1024)
        {
            throw new ArgumentException("Invalid encoded image size.");
        }

        if (bytes.Length > 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
        {
            long w = U32(bytes, 16),
                h = U32(bytes, 20);
            if (w < 1 || h < 1 || w > 12000 || h > 12000 || w * h > 16000000)
            {
                throw new ArgumentException("PNG dimensions exceed image limits.");
            }
        }

        using var raw = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (raw.Empty() || raw.Depth() != MatType.CV_8U || (long)raw.Rows * raw.Cols > 16000000)
        {
            throw new ArgumentException("Unsupported decoded image.");
        }

        if (raw.Channels() == 4)
        {
            using var color = new Mat(raw.Rows, raw.Cols, MatType.CV_8UC3);
            for (int y = 0; y < raw.Rows; y++)
            {
                for (int x = 0; x < raw.Cols; x++)
                {
                    var v = raw.At<Vec4b>(y, x);
                    color.Set(
                        y,
                        x,
                        new Vec3b(
                            (byte)((v.Item0 * v.Item3 + 255 * (255 - v.Item3)) / 255),
                            (byte)((v.Item1 * v.Item3 + 255 * (255 - v.Item3)) / 255),
                            (byte)((v.Item2 * v.Item3 + 255 * (255 - v.Item3)) / 255)
                        )
                    );
                }
            }

            return CvImages.Frame(color);
        }

        return CvImages.Frame(raw);
    }

    /// <inheritdoc/>
    public byte[] EncodePng(ImageFrame frame)
    {
        using var raw = CvImages.Mat(frame);
        return raw.ToBytes(".png");
    }

    /// <summary>绘制实际图像坐标证据，不修改生产原始像素。</summary>
    /// <param name = "frame">不可变实际原图，不修改其像素。</param>
    /// <param name = "report">已完成报告，证据定位使用实际图像坐标。</param>
    public ImageFrame Annotate(ImageFrame frame, InspectionReport report)
    {
        using var raw = CvImages.Mat(frame);
        using var canvas = new Mat();
        if (raw.Channels() == 1)
        {
            Cv2.CvtColor(raw, canvas, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            raw.CopyTo(canvas);
        }

        foreach (var region in report.Analysis.Regions)
        {
            if (region.Segmentation != null)
            {
                foreach (var character in region.Segmentation.Characters)
                {
                    if (character.Bounds.Fits(frame))
                    {
                        Cv2.Rectangle(canvas, CvImages.Rect(character.Bounds), new Scalar(80, 180, 20), 1);
                    }
                }
            }
        }

        foreach (var group in report.EvidenceGroups)
        {
            var finding = group.Summary;
            if (!finding.Bounds.HasValue || !finding.Bounds.Value.Fits(frame))
            {
                continue;
            }

            var bounds = finding.Bounds.Value;
            var color =
                finding.Verdict == EInspectionVerdict.Ng ? new Scalar(0, 0, 255) : new Scalar(0, 160, 255);
            Cv2.Rectangle(canvas, CvImages.Rect(bounds), color, 2);
            if (group.IsBarcode)
            {
                Cv2.PutText(
                    canvas,
                    group.Id + " " + finding.Verdict.ToString().ToUpperInvariant(),
                    new Point(bounds.X, Math.Max(12, bounds.Y - 4)),
                    HersheyFonts.HersheySimplex,
                    .5,
                    color,
                    1
                );
            }
        }

        return CvImages.Frame(canvas);
    }

    private static long U32(byte[] b, int n)
    {
        return ((long)b[n] << 24) | ((long)b[n + 1] << 16) | ((long)b[n + 2] << 8) | b[n + 3];
    }
}
