using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>一个明确选定的独立字符参考。</summary>
public sealed class GlyphImportItem
{
    /// <summary>校验当前字库字符与尺寸契约，源图像保持不可变。</summary>
    /// <param name = "character">支持的独立ASCII字母或数字标签，大小写敏感。</param>
    /// <param name = "image">独立不可变参考图块。</param>
    /// <param name = "binarization">二值化模式：otsu自动阈值或fixed固定阈值。</param>
    /// <param name = "provenanceJson">可选来源证据JSON，用于保留人工确认及原图来源。</param>
    public GlyphImportItem(
        string character,
        ImageFrame image,
        string binarization = "otsu",
        string? provenanceJson = null
    )
    {
        _ = new GlyphReference(character, image, "", binarization);
        Character = character;
        Image = image;
        Binarization = binarization;
        ProvenanceJson = provenanceJson;
    }

    /// <summary>一个受支持的独立字符。</summary>
    public string Character { get; }

    /// <summary>独立拥有像素的不可变图块。</summary>
    public ImageFrame Image { get; }

    /// <summary>参考二值化策略。</summary>
    public string Binarization { get; }

    /// <summary>可选的来源证据JSON。</summary>
    public string? ProvenanceJson { get; }
}
