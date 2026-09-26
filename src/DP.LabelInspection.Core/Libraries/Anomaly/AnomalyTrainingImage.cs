using System;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>批量训练页中的一张良品图。</summary>
public sealed class AnomalyTrainingImage
{
    internal AnomalyTrainingImage(ImageFrame image, string name, string? path)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Name = string.IsNullOrWhiteSpace(name) ? "图像" : name;
        Path = path;
    }

    /// <summary>不可变整图。</summary>
    public ImageFrame Image { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>来源文件路径；没有时（如当前图像）保存采集会另存图像。</summary>
    public string? Path { get; internal set; }

    /// <summary>显示名称。</summary>
    public override string ToString()
    {
        return Name;
    }
}
