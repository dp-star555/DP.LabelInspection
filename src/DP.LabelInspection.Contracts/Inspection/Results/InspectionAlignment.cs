using System;
using DP.Vision.Algorithms;

namespace DP.LabelInspection.Contracts;

/// <summary>一次检测中配方坐标到实际原图坐标的仿射事实；帧身份与矩阵一同保存，不能跨帧复用。</summary>
public sealed class InspectionAlignment
{
    /// <summary>没有提供定位数据时声明坐标已对齐；使用恒等矩阵，不宣称已实测定位。</summary>
    /// <param name="frameId">本次实际图像身份。</param>
    public InspectionAlignment(string frameId)
        : this(frameId, CoordinateMatrix2D.Identity, false) { }

    /// <summary>创建同帧可逆的定位事实。</summary>
    /// <param name="frameId">本次实际图像身份。</param>
    /// <param name="recipeToImage">配方像素边界坐标到当前原图像素边界坐标。</param>
    /// <param name="measured">true表示由本次图像定位得到；false表示宿主声明图像已对齐。</param>
    public InspectionAlignment(string frameId, CoordinateMatrix2D recipeToImage, bool measured)
    {
        if (string.IsNullOrWhiteSpace(frameId))
            throw new ArgumentException("Frame identity required.", nameof(frameId));
        RecipeToImage = recipeToImage ?? throw new ArgumentNullException(nameof(recipeToImage));
        ImageToRecipe = recipeToImage.Inverse();
        FrameId = frameId;
        Measured = measured;
    }

    /// <summary>本次原图身份；不能用尺寸相同推断是同一帧。</summary>
    public string FrameId { get; }

    /// <summary>是否已从图像测得定位；false为声明对齐，不是测量成功。</summary>
    public bool Measured { get; }

    /// <summary>配方到当前原图的变换，不仅能表示平移。</summary>
    public CoordinateMatrix2D RecipeToImage { get; }

    /// <summary>当前原图到配方坐标的逆变换。</summary>
    public CoordinateMatrix2D ImageToRecipe { get; }
}
