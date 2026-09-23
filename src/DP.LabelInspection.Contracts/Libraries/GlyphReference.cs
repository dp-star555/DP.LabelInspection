using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>单个不可变参考；PNG哈希对应已存储字节，而不是重新编码结果。</summary>
public sealed class GlyphReference
{
    /// <summary>创建经过校验的参考。</summary>
    /// <param name = "character">大小写敏感的独立ASCII字母或数字标签。</param>
    /// <param name = "image">不可变参考图块。</param>
    /// <param name = "sha256">已保存PNG原始字节的SHA256，不是重新编码结果的哈希。</param>
    /// <param name = "binarization">二值化模式，otsu自动阈值或fixed固定阈值。</param>
    public GlyphReference(string character, ImageFrame image, string sha256, string binarization = "otsu")
    {
        if (
            character == null
            || character.Length != 1
            || !FieldSettings.IsAlphanumeric(character[0])
            || image == null
            || image.Width < 4
            || image.Height < 4
            || image.Width > 512
            || image.Height > 512
            || (binarization != "otsu" && binarization != "fixed")
        )
        {
            throw new ArgumentException("Invalid glyph reference.");
        }

        Character = character;
        Image = image;
        Sha256 = sha256;
        Binarization = binarization;
    }

    /// <summary>大小写敏感的独立字符标签。</summary>
    public string Character { get; }

    /// <summary>参考像素。</summary>
    public ImageFrame Image { get; }

    /// <summary>已存PNG字节的哈希。</summary>
    public string Sha256 { get; }

    /// <summary>二值化模式：otsu自动阈值或fixed固定阈值。</summary>
    public string Binarization { get; }
}
