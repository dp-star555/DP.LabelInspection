using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 逐字符异常模型的一行训练样本：原图及该行全部已确认身份的字符。整行字符用于测量行高与基线（字符按行几何归一化，
/// 不按各自墨迹框缩放），<see cref = "Excluded"/>中的字符只参与行几何，不作为训练样本。
/// </summary>
public sealed class CharacterAnomalySample
{
    /// <summary>创建一行样本。</summary>
    /// <param name = "image">借用的原始整图（良品）。</param>
    /// <param name = "characters">该行按顺序的字符，身份须已人工确认；原图坐标。</param>
    /// <param name = "excluded">不作为训练样本的字符序号（在<paramref name = "characters"/>中的下标），例如切割可疑或身份不确定的字。</param>
    /// <param name = "source">可选来源说明（文件名等）。</param>
    /// <param name = "group">字符组（同一字体的行共用；null为不分组），同组同字符的样本合训一个模型。</param>
    public CharacterAnomalySample(
        ImageFrame image,
        IEnumerable<CharacterPatch> characters,
        IEnumerable<int>? excluded = null,
        string? source = null,
        string? group = null
    )
    {
        if (group != null && !AnomalyModelEntry.IsCharacterGroup(group))
        {
            throw new ArgumentException("字符组名称须为1–60字符且不含“/”。", nameof(group));
        }

        Group = group;
        Image = image ?? throw new ArgumentNullException(nameof(image));
        var list = (characters ?? throw new ArgumentNullException(nameof(characters))).ToArray();
        if (list.Length == 0 || list.Any(c => c == null))
        {
            throw new ArgumentException("A line needs characters.", nameof(characters));
        }

        Characters = new ReadOnlyCollection<CharacterPatch>(list);
        var skip = (excluded ?? Array.Empty<int>()).Distinct().OrderBy(i => i).ToArray();
        if (skip.Any(i => i < 0 || i >= list.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(excluded));
        }

        Excluded = new ReadOnlyCollection<int>(skip);
        Source = source;
    }

    /// <summary>原始整图。</summary>
    public ImageFrame Image { get; }

    /// <summary>该行字符（原图坐标）。</summary>
    public IReadOnlyList<CharacterPatch> Characters { get; }

    /// <summary>不作为训练样本的字符下标。</summary>
    public IReadOnlyList<int> Excluded { get; }

    /// <summary>可选来源说明。</summary>
    public string? Source { get; }

    /// <summary>字符组；null为不分组。</summary>
    public string? Group { get; }
}
