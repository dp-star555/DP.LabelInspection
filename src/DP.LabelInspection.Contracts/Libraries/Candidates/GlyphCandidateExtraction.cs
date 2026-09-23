using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>供人工复核参考制作使用的OCR/物理分割候选，不直接写入字库。</summary>
public sealed class GlyphCandidateExtraction
{
    /// <summary>创建不可变提取结果；ConfirmedText记录明确人工标签，不是修正后的原始读取。</summary>
    /// <param name = "recognition">原始OCR证据，未执行OCR时为null。</param>
    /// <param name = "confirmedText">明确人工标签，null表示采用OCR身份，不覆盖recognition。</param>
    /// <param name = "segmentation">真实物理分割或候选结果及其不确定原因。</param>
    public GlyphCandidateExtraction(
        TextLineRecognition? recognition,
        string? confirmedText,
        CharacterSegmentation segmentation
    )
    {
        Recognition = recognition;
        ConfirmedText = confirmedText;
        Segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
    }

    /// <summary>若执行了OCR，则保留未经改写的读取结果。</summary>
    public TextLineRecognition? Recognition { get; }

    /// <summary>人工明确提供的整行文本；null表示采用OCR身份。</summary>
    public string? ConfirmedText { get; }

    /// <summary>基于图像的独立候选及不确定原因。</summary>
    public CharacterSegmentation Segmentation { get; }
}
