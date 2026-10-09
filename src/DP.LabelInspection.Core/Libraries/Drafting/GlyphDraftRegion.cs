using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>当前来源图上的独立单行制作区域；身份、人工文字与提取状态不与其他区域混用。</summary>
public sealed class GlyphDraftRegion
{
    internal GlyphDraftRegion(
        string id,
        string name,
        PixelRect bounds,
        string? confirmedText = null,
        GlyphCandidateExtraction? extraction = null,
        string? error = null
    )
    {
        Id = id;
        Name = name;
        Bounds = bounds;
        ConfirmedText = confirmedText;
        Extraction = extraction;
        Error = error;
    }

    /// <summary>当前来源图内的稳定区域标识，删除后不复用。</summary>
    public string Id { get; }

    /// <summary>供制作界面显示的ROI序号名称。</summary>
    public string Name { get; }

    /// <summary>原图中的水平单行整数范围。</summary>
    public PixelRect Bounds { get; }

    /// <summary>该区域明确输入的人工文字，null表示批量提取时使用OCR。</summary>
    public string? ConfirmedText { get; }

    /// <summary>该区域最近一次提取证据，不能当作已批准良品参考。</summary>
    public GlyphCandidateExtraction? Extraction { get; }

    /// <summary>最近提取失败原因；失败时已有候选仍保留待人工核对。</summary>
    public string? Error { get; }

    /// <summary>人工文字优先，否则显示该区域最近OCR原始读数。</summary>
    public string DisplayText => ConfirmedText ?? Extraction?.Recognition?.Text ?? "";

    /// <summary>用于区域选择器的简短状态，不包含敏感来源路径。</summary>
    /// <returns>区域名称及提取状态。</returns>
    public override string ToString() =>
        Name
        + " · "
        + (
            Error != null ? "失败（候选保留）"
            : Extraction == null ? "待提取"
            : Extraction.Segmentation.Characters.Count + "候选"
        );
}
