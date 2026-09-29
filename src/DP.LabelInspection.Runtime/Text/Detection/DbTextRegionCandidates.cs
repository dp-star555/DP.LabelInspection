using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.OnnxDetection;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

/// <summary>把DB概率图变成保守的水平文本候选；不含模型推理，也不做标签业务判定。</summary>
/// <remarks>阈值、筛选顺序与去重规则与迁移前的检测器逐条一致，不得在迁移时重新调参。</remarks>
public static class DbTextRegionCandidates
{
    /// <summary>二值化阈值；低于该值的概率不参与轮廓提取。</summary>
    public const float BinarizationThreshold = .3f;

    /// <summary>候选区域内平均概率下限。</summary>
    public const double MeanProbabilityFloor = .6;

    /// <summary>候选数量上限。</summary>
    public const int MaxCandidates = 64;

    /// <summary>参与筛选的轮廓数量上限，按面积降序取前若干个。</summary>
    public const int MaxContours = 1000;

    /// <summary>从概率图提取原图坐标候选，并按先Y后X排序。</summary>
    /// <param name = "evidence">模型身份、概率图与几何映射。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>原图坐标的保守水平候选；不含旋转或透视文本。</returns>
    public static IReadOnlyList<PixelRect> Extract(
        PPOcrDetectionOutput evidence,
        CancellationToken token = default
    )
    {
        if (evidence == null)
        {
            throw new ArgumentNullException(nameof(evidence));
        }

        token.ThrowIfCancellationRequested();
        int width = evidence.Width,
            height = evidence.Height,
            imageWidth = evidence.ImageWidth,
            imageHeight = evidence.ImageHeight;
        var probability = evidence.CopyValues();
        using var mask = new Mat(height, width, MatType.CV_8UC1);
        for (int i = 0; i < probability.Length; i++)
        {
            mask.Set(i / width, i % width, probability[i] > BinarizationThreshold ? (byte)255 : (byte)0);
        }

        Cv2.FindContours(
            mask,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple
        );
        var regions = new List<PixelRect>();
        foreach (var contour in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(MaxContours))
        {
            token.ThrowIfCancellationRequested();
            var box = Cv2.BoundingRect(contour);
            if (box.Width < 4 || box.Height < 3 || box.Width < box.Height * 1.3)
            {
                continue;
            }

            var oriented = Cv2.MinAreaRect(contour);
            var vertices = oriented.Points();
            double longest = 0,
                slope = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % 4];
                double dx = b.X - a.X,
                    dy = b.Y - a.Y,
                    length = dx * dx + dy * dy;
                if (length > longest)
                {
                    longest = length;
                    slope = Math.Abs(dy) / Math.Max(.001, Math.Abs(dx));
                }
            }

            if (slope > Math.Tan(Math.PI / 12))
            {
                continue;
            }

            using var area = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillPoly(area, new[] { contour }, Scalar.All(255));
            double sum = 0;
            int n = 0;
            for (int y = box.Y; y < box.Bottom; y++)
            {
                for (int x = box.X; x < box.Right; x++)
                {
                    if (area.At<byte>(y, x) != 0)
                    {
                        sum += probability[y * width + x];
                        n++;
                    }
                }
            }

            if (n == 0 || sum / n < MeanProbabilityFloor)
            {
                continue;
            }

            double pad = box.Width * box.Height * 1.5 / (2.0 * (box.Width + box.Height));
            int left = Math.Max(0, (int)Math.Floor((box.Left - pad) * imageWidth / width)),
                top = Math.Max(0, (int)Math.Floor((box.Top - pad) * imageHeight / height));
            int right = Math.Min(imageWidth, (int)Math.Ceiling((box.Right + pad) * imageWidth / width)),
                bottom = Math.Min(
                    imageHeight,
                    (int)Math.Ceiling((box.Bottom + pad) * imageHeight / height)
                );
            if (right - left < 4 || bottom - top < 4 || bottom - top > 512 || right - left > 6000)
            {
                continue;
            }

            var candidate = new PixelRect(left, top, right - left, bottom - top);
            if (
                regions.Any(r =>
                    Intersection(r, candidate)
                    > .7 * Math.Min((long)r.Width * r.Height, (long)candidate.Width * candidate.Height)
                )
            )
            {
                continue;
            }

            regions.Add(candidate);
            if (regions.Count == MaxCandidates)
            {
                break;
            }
        }

        return regions.OrderBy(r => r.Y).ThenBy(r => r.X).ToArray();
    }

    private static long Intersection(PixelRect a, PixelRect b)
    {
        return Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * (long)Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
    }
}
