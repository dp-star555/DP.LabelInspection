using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>带编号的子观测，保留未经修改的原始证据。</summary>
public sealed class InspectionEvidenceDetail
{
    /// <summary>创建用于可移植序列化的不可变子观测。</summary>
    /// <param name = "id">本报告内的子项标识，例如F1.2。</param>
    /// <param name = "finding">未经修改的原始诊断证据。</param>
    public InspectionEvidenceDetail(string id, InspectionFinding finding)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Child ID is required.", nameof(id));
        }

        Id = id;
        Finding = finding ?? throw new ArgumentNullException(nameof(finding));
    }

    /// <summary>报告内的子项标识，例如F1.2。</summary>
    public string Id { get; }

    /// <summary>原始诊断代码、严重程度、坐标、面积和描述。</summary>
    public InspectionFinding Finding { get; }

    /// <summary>子项可读状态，不通过解析说明文本推导。</summary>
    public string Status => Finding.Verdict.ToString().ToUpperInvariant();

    /// <summary>该记录是否包含实际局部外观证据，而非仅有失败范围。</summary>
    public bool IsLocalizedCandidate =>
        !IsComparisonBlocker
        && Finding.Verdict != EInspectionVerdict.Ok
        && (Finding.AreaPixels > 0 || Finding.Code == "glyph_exceeds_threshold");

    /// <summary>该记录是否表示无法完成要求的比较。</summary>
    public bool IsComparisonBlocker =>
        Finding.IsExecutionBlocker
        || Finding.Code == "quality_incomplete"
        || Finding.Code == "roi_execution_failed"
        || Finding.Code == "quality_completion_unavailable"
        || Finding.Code == "binding_unavailable"
        || Finding.Code == "data_not_enabled"
        || Finding.Code == "ocr_unreliable"
        || Finding.Code == "barcode_unavailable"
        || Finding.Code == "missing_reference"
        || Finding.Code == "roi_outside_image"
        || Finding.Code == "roi_overlap"
        || Finding.Code == "empty_check_scope"
        || Finding.Code == "empty_effective_scope"
        || Finding.Code == "no_roi_tasks"
        || Finding.Code == "appearance_incomplete"
        || Finding.Code == "segmentation_review"
        || Finding.Code == "missing_template"
        || Finding.Code == "no_character_coverage"
        || Finding.Code == "ocr_unavailable"
        || Finding.Code == "text_layout_required"
        || Finding.Code.StartsWith("glyph_library_", StringComparison.Ordinal)
        || Finding.Code == "glyph_repository_unavailable"
        || Finding.Code.StartsWith("glyph_empty_", StringComparison.Ordinal);
}
