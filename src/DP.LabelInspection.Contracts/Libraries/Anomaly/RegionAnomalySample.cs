using System;

namespace DP.LabelInspection.Contracts;

/// <summary>整ROI异常模型的一个训练样本：原图及样本框（原图坐标）。同一模型的样本可来自不同图、不同位置。</summary>
public sealed class RegionAnomalySample
{
    /// <summary>创建样本。</summary>
    /// <param name = "image">借用的原始整图（良品）。</param>
    /// <param name = "bounds">样本框，须在图像内；位置相关模型要求各样本框尺寸相同。</param>
    /// <param name = "source">可选来源说明（文件名等）。</param>
    public RegionAnomalySample(ImageFrame image, PixelRect bounds, string? source = null)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        if (!bounds.Fits(image) || bounds.Width < 8 || bounds.Height < 8)
        {
            throw new ArgumentException(
                "Sample box must lie inside the image and be at least 8×8.",
                nameof(bounds)
            );
        }

        Bounds = bounds;
        Source = source;
    }

    /// <summary>原始整图。</summary>
    public ImageFrame Image { get; }

    /// <summary>样本框（原图坐标）。</summary>
    public PixelRect Bounds { get; }

    /// <summary>可选来源说明。</summary>
    public string? Source { get; }
}
