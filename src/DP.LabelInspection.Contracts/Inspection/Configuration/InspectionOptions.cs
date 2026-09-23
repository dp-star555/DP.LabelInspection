using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>不可变的初始图像差异参数，生产使用需要调校。</summary>
public sealed class InspectionOptions
{
    /// <summary>创建经过校验的参数。</summary>
    /// <param name = "inkThreshold">灰度严格低于该值的像素视为墨迹。</param>
    /// <param name = "tolerancePixels">形态学半径，单位为原图像素。</param>
    /// <param name = "minimumDefectArea">最小连通域面积，单位为原图平方像素。</param>
    /// <param name = "minimumContrast">最小1%/99%分位灰度差。</param>
    /// <param name = "minimumSharpness">最小拉普拉斯方差。</param>
    public InspectionOptions(
        int inkThreshold = 160,
        int tolerancePixels = 1,
        int minimumDefectArea = 8,
        double minimumContrast = 35,
        double minimumSharpness = 25
    )
    {
        if (
            inkThreshold < 1
            || inkThreshold > 255
            || tolerancePixels < 0
            || tolerancePixels > 10
            || minimumDefectArea < 1
        )
        {
            throw new ArgumentOutOfRangeException(nameof(inkThreshold));
        }

        if (
            double.IsNaN(minimumContrast)
            || double.IsInfinity(minimumContrast)
            || minimumContrast < 0
            || minimumContrast > 255
            || double.IsNaN(minimumSharpness)
            || double.IsInfinity(minimumSharpness)
            || minimumSharpness < 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(minimumContrast));
        }

        InkThreshold = inkThreshold;
        TolerancePixels = tolerancePixels;
        MinimumDefectArea = minimumDefectArea;
        MinimumContrast = minimumContrast;
        MinimumSharpness = minimumSharpness;
    }

    /// <summary>严格灰度阈值。</summary>
    public int InkThreshold { get; }

    /// <summary>原图像素半径。</summary>
    public int TolerancePixels { get; }

    /// <summary>原图像素面积过滤阈值。</summary>
    public int MinimumDefectArea { get; }

    /// <summary>图像质量对比度阈值。</summary>
    public double MinimumContrast { get; }

    /// <summary>图像质量清晰度阈值。</summary>
    public double MinimumSharpness { get; }
}
