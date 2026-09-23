using System;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>要求的外观覆盖属于执行完成要求，不是待确认的视觉缺陷。</summary>
internal static class RequiredAppearancePolicy
{
    internal static BackendAnalysis Apply(InspectionRequest request, BackendAnalysis analysis)
    {
        var results = analysis.Regions.ToList();
        foreach (
            var roi in request.Recipe.Regions.Where(r =>
                r.Kind == ERegionKind.Text && (r.Field.LibraryId != null || r.Field.EqualCells)
            )
        )
        {
            int index = results.FindIndex(r => r.RegionName == roi.Name);
            var result =
                index < 0
                    ? new RegionInspectionResult(roi.Name, Array.Empty<InspectionFinding>())
                    : results[index];
            int total = result.Segmentation?.Characters.Count ?? 0;
            int completed = result.Glyphs.Count(g =>
                (g.Status == "compared" || g.Status == "exceeds_threshold")
                && g.Comparison?.Status == "compared"
            );
            bool validSegmentation =
                result.Segmentation != null
                && (
                    result.Segmentation.Status == "provisional"
                    || result.Segmentation.Status == "explicit_cells"
                );
            bool complete =
                validSegmentation && total > 0 && result.Glyphs.Count == total && completed == total;
            if (complete)
            {
                continue;
            }

            bool Blocker(string code)
            {
                return code == "segmentation_review"
                    || code == "missing_template"
                    || code == "no_character_coverage"
                    || code == "ocr_unavailable"
                    || code == "text_layout_required"
                    || code.StartsWith("glyph_library_", StringComparison.Ordinal)
                    || code == "glyph_repository_unavailable"
                    || code.StartsWith("glyph_empty_", StringComparison.Ordinal);
            }

            var findings = result
                .Findings.Select(f =>
                    Blocker(f.Code)
                        ? new InspectionFinding(
                            f.Code,
                            f.Message,
                            EInspectionVerdict.Ng,
                            f.Bounds,
                            f.AreaPixels
                        )
                        : f
                )
                .ToList();
            findings.Add(
                new InspectionFinding(
                    "appearance_incomplete",
                    "已要求单字外观比对，但未完整执行：分割字块 "
                        + total
                        + "，完成比较 "
                        + completed
                        + "。按必检策略判NG；这是比对阻断，不代表已定位相同数量的印刷缺陷。请查看分割、缺字、识别身份或参考可用性明细。",
                    EInspectionVerdict.Ng,
                    result.Recognition?.Bounds ?? roi.Bounds
                )
            );
            var next = new RegionInspectionResult(
                result.RegionName,
                findings,
                result.Recognition,
                result.Segmentation,
                result.Glyphs,
                result.Barcodes
            );
            if (index < 0)
            {
                results.Add(next);
            }
            else
            {
                results[index] = next;
            }
        }

        return new BackendAnalysis(
            analysis.Contrast,
            analysis.Sharpness,
            results,
            analysis.OffsetX,
            analysis.OffsetY
        );
    }
}
