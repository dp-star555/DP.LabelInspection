using System;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Runtime;

/// <summary>按逐字符训练方式归一化的一个字符单元（用于与其他算法对比评估）。</summary>
public sealed class CharacterAnomalyCell
{
    /// <summary>创建单元。</summary>
    /// <param name = "sample">所属行样本的序号（训练样本在前，其余样本接续编号）。</param>
    /// <param name = "index">在该行字符列表中的序号。</param>
    /// <param name = "key">模型键（“组/字符”或字符）。</param>
    /// <param name = "training">是否来自训练样本。</param>
    /// <param name = "image">归一化后的灰度单元，与同键训练单元同尺寸。</param>
    public CharacterAnomalyCell(int sample, int index, string key, bool training, ImageFrame image)
    {
        Sample = sample;
        Index = index;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Training = training;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <summary>所属行样本的序号。</summary>
    public int Sample { get; }

    /// <summary>在该行字符列表中的序号。</summary>
    public int Index { get; }

    /// <summary>模型键。</summary>
    public string Key { get; }

    /// <summary>是否来自训练样本。</summary>
    public bool Training { get; }

    /// <summary>归一化后的灰度单元。</summary>
    public ImageFrame Image { get; }
}
