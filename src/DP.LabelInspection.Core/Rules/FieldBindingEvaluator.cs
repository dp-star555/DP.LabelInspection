using System;
using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

internal static class FieldBindingEvaluator
{
    internal static BackendAnalysis Apply(
        InspectionRequest request,
        BackendAnalysis analysis,
        bool quality,
        DateTimeOffset now,
        EInspectionCapabilities capabilities
    )
    {
        if (request.Recipe.Bindings.Count == 0)
        {
            return analysis;
        }

        var results = analysis.Regions.ToArray();
        bool Read(string name, out string? text, out string reason)
        {
            text = null;
            var region = request.Recipe.Regions.Single(r => r.Name == name);
            var observations = results.Where(r => r.RegionName == name).ToArray();
            if (observations.Length != 1)
            {
                reason = "区域结果缺失或不唯一";
                return false;
            }

            var result = observations[0];
            if (
                (
                    capabilities
                    & (
                        region.Kind == ERegionKind.Text
                            ? EInspectionCapabilities.Ocr
                            : EInspectionCapabilities.BarcodeDecode
                    )
                ) == 0
            )
            {
                reason = "后端未提供对应识别/解码能力";
                return false;
            }

            if (region.Kind == ERegionKind.Text)
            {
                text = result.Recognition?.Text;
                if (string.IsNullOrEmpty(text))
                {
                    reason = "未取得OCR读数";
                    return false;
                }

                if (result.Recognition!.Confidence < region.Field.MinimumConfidence)
                {
                    reason = "OCR置信度不足";
                    return false;
                }
            }
            else
            {
                if (result.Barcodes.Count != 1)
                {
                    reason = "条码未解出或存在多个符号，不能确定绑定值";
                    return false;
                }

                text = result.Barcodes[0].Text;
                if (string.IsNullOrEmpty(text))
                {
                    reason = "条码数据为空";
                    return false;
                }
            }

            reason = "";
            return true;
        }

        var mapped = new List<RegionInspectionResult>();
        foreach (var result in results)
        {
            var bindings = request.Recipe.Bindings.Where(b => b.Target == result.RegionName).ToArray();
            if (bindings.Length == 0)
            {
                mapped.Add(result);
                continue;
            }

            var findings = result.Findings.Where(f => f.Code != "ocr_identity_review").ToList();
            foreach (var binding in bindings)
            {
                bool reliable = Read(binding.Target, out var actual, out var reason);
                string? expected = null;
                if (binding.Source == EBindingSource.Region)
                {
                    bool sourceOk = Read(binding.Key, out expected, out var sourceReason);
                    if (!sourceOk)
                    {
                        reason += " 来源：" + sourceReason;
                    }

                    reliable &= sourceOk;
                }
                else
                {
                    var data = request.TaskData;
                    if (data == null)
                    {
                        reliable = false;
                        reason += " 未提供本次任务数据";
                    }
                    else
                    {
                        data.Values.TryGetValue(binding.Key, out expected);
                        if (
                            string.IsNullOrWhiteSpace(request.CycleId)
                            || !string.Equals(request.CycleId, data.CycleId, StringComparison.Ordinal)
                        )
                        {
                            reliable = false;
                            reason += " 图像与数据的检测周期编号不一致";
                        }

                        if (now < data.CapturedAt || now > data.ValidUntil)
                        {
                            reliable = false;
                            reason += " 数据未生效或已过期";
                        }

                        if (expected == null)
                        {
                            reliable = false;
                            reason += " 任务字段缺失";
                        }
                    }
                }

                if (!quality)
                {
                    reliable = false;
                    reason += " 图像质量不足";
                }

                bool equal =
                    actual != null
                    && expected != null
                    && string.Equals(actual, expected, StringComparison.Ordinal);
                string code =
                    !reliable ? "binding_review"
                    : equal ? "binding_match"
                    : "binding_mismatch";
                string meaning =
                    binding.Source == EBindingSource.Region
                        ? "仅交叉一致性，不是独立业务真值"
                        : "与本次任务预期比较";
                string message =
                    $"{binding.Target} <- {binding.Source}:{binding.Key}；读数=[{actual ?? "未取得"}]；来源值=[{expected ?? "未取得"}]；{meaning}；"
                    + (
                        !reliable ? reason
                        : equal ? "匹配"
                        : "不匹配"
                    );
                var bounds =
                    result.Recognition?.Bounds
                    ?? (result.Barcodes.Count == 1 ? (PixelRect?)result.Barcodes[0].Bounds : null);
                findings.Add(
                    new InspectionFinding(
                        code,
                        message,
                        !reliable ? EInspectionVerdict.Review
                            : equal ? EInspectionVerdict.Ok
                            : EInspectionVerdict.Ng,
                        bounds
                    )
                );
            }

            mapped.Add(
                new RegionInspectionResult(
                    result.RegionName,
                    findings,
                    result.Recognition,
                    result.Segmentation,
                    result.Glyphs,
                    result.Barcodes
                )
            );
        }

        return new BackendAnalysis(
            analysis.Contrast,
            analysis.Sharpness,
            mapped,
            analysis.OffsetX,
            analysis.OffsetY
        );
    }
}
