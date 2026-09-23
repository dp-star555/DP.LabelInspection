using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>拥有像素的实际字符图块；范围可能与邻字重叠，应比较Patch，不能按Bounds重新裁图。</summary>
public sealed class CharacterPatch
{
    /// <summary>创建原图坐标中的字符证据。</summary>
    /// <param name = "character">待确认身份或显式等格标签。</param>
    /// <param name = "tokenIndex">在非空白标签序列中的索引。</param>
    /// <param name = "bounds">原图像素范围，可能与邻字外接框重叠。</param>
    /// <param name = "patch">独立不可变图块，不应根据范围重新裁图。</param>
    /// <param name = "neighborInkRemoved">已移除的已知邻字墨迹量，单位为原图像素。</param>
    public CharacterPatch(
        string character,
        int tokenIndex,
        PixelRect bounds,
        ImageFrame patch,
        int neighborInkRemoved = 0
    )
    {
        if (
            character == null
            || character.Length != 1
            || !FieldSettings.IsAlphanumeric(character[0])
            || tokenIndex < 0
            || neighborInkRemoved < 0
        )
        {
            throw new ArgumentException("Invalid character.");
        }

        Character = character;
        TokenIndex = tokenIndex;
        Bounds = bounds;
        Patch = patch ?? throw new ArgumentNullException(nameof(patch));
        NeighborInkRemoved = neighborInkRemoved;
    }

    /// <summary>OCR假设或明确预期的等格字符标签。</summary>
    public string Character { get; }

    /// <summary>去除空白后的字符序列位置。</summary>
    public int TokenIndex { get; }

    /// <summary>原图像素范围。</summary>
    public PixelRect Bounds { get; }

    /// <summary>已抑制已知邻字墨迹的独立图像。</summary>
    public ImageFrame Patch { get; }

    /// <summary>移除的已知邻字墨迹量，单位为原图像素。</summary>
    public int NeighborInkRemoved { get; }
}
