using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 逐字符异常模型的一行训练样本：原图及该行全部训练身份的字符。提供行ROI时采用稳定行几何，历史未提供时测量墨迹行高与基线（
/// 不按各自墨迹框缩放），<see cref = "Excluded"/>中的字符只参与行几何，不作为训练样本。
/// </summary>
public sealed class CharacterAnomalySample
{
    /// <summary>创建一行样本。</summary>
    /// <param name = "image">借用的原始整图（良品）。</param>
    /// <param name = "characters">该行按顺序的Unicode单字与原图坐标；用户负责良品来源和标签真实性。</param>
    /// <param name = "excluded">不作为训练样本的字符序号（在<paramref name = "characters"/>中的下标），例如切割可疑或身份不确定的字。</param>
    /// <param name = "source">可选来源说明（文件名等）。</param>
    /// <param name = "group">字符组（同一字体的行共用；null为不分组），同组同字符的样本合训一个模型。</param>
    /// <param name="lineBounds">可选稳定单行ROI，指定后按整行范围归一化，纯标点也可训练。</param>
    public CharacterAnomalySample(
        PixelSnapshot image,
        IEnumerable<CharacterPatch> characters,
        IEnumerable<int>? excluded = null,
        string? source = null,
        string? group = null,
        PixelRect? lineBounds = null
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

        if (
            lineBounds is PixelRect bounds
            && (
                !bounds.Fits(image)
                || bounds.Width < 4
                || bounds.Height < 4
                || list.Any(c =>
                    c.Bounds.X < bounds.X
                    || c.Bounds.Y < bounds.Y
                    || (long)c.Bounds.X + c.Bounds.Width > (long)bounds.X + bounds.Width
                    || (long)c.Bounds.Y + c.Bounds.Height > (long)bounds.Y + bounds.Height
                )
            )
        )
            throw new ArgumentException("制作行ROI须位于原图内并包含本行全部字符。", nameof(lineBounds));
        LineBounds = lineBounds;
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
    public PixelSnapshot Image { get; }

    /// <summary>该行字符（原图坐标）。</summary>
    public IReadOnlyList<CharacterPatch> Characters { get; }

    /// <summary>不作为训练样本的字符下标。</summary>
    public IReadOnlyList<int> Excluded { get; }

    /// <summary>可选来源说明。</summary>
    public string? Source { get; }

    /// <summary>稳定原图单行ROI；null保留历史墨迹行几何。</summary>
    public PixelRect? LineBounds { get; }

    /// <summary>字符组；null为不分组。</summary>
    public string? Group { get; }
}
