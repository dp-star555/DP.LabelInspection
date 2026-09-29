using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>
/// 内容核对规则（与调度无关）：实际读数的取值、码制匹配、任务引导数据有效性、格式约束及与引导值的精确比较。
/// 比较一律按原始字符串逐字符精确进行，不做O/0、大小写等纠正；不一致生成NG发现，不修改读数。
/// </summary>
public static class ContentVerification
{
    private static readonly string[] LinearFormats =
    {
        "CODE_128",
        "CODE_39",
        "CODE_93",
        "EAN_13",
        "EAN_8",
        "UPC_A",
        "UPC_E",
        "ITF",
        "CODABAR",
        "MSI",
        "PLESSEY",
        "RSS_14",
        "RSS_EXPANDED",
    };

    /// <summary>读取结果中的唯一实际读数：文字取OCR文本，码取唯一一个码的内容；没有或不唯一时返回false。</summary>
    /// <param name = "reading">读取阶段的结果。</param>
    /// <param name = "value">实际读数。</param>
    public static bool TryGetValue(RegionInspectionResult reading, out string? value)
    {
        if (reading == null)
        {
            throw new ArgumentNullException(nameof(reading));
        }

        value = reading.Recognition?.Text ?? (reading.Barcodes.Count == 1 ? reading.Barcodes[0].Text : null);
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>实际码制是否符合ROI配置的码类型（自动时任意码制都符合）。</summary>
    /// <param name = "format">解码器报告的码制名称，如 <c>CODE_128</c>、<c>QR_CODE</c>。</param>
    /// <param name = "kind">ROI配置的码类型。</param>
    public static bool BarcodeKindMatches(string format, EBarcodeKind kind)
    {
        return kind == EBarcodeKind.Auto
            || (kind == EBarcodeKind.QrCode ? format == "QR_CODE" : LinearFormats.Contains(format));
    }

    /// <summary>取本周期任务引导数据中的字段：须提供数据、周期标识一致、在有效期内且含该字段。</summary>
    /// <param name = "request">本轮请求（周期标识与任务数据快照）。</param>
    /// <param name = "key">任务字段名。</param>
    /// <param name = "now">判断有效期所用的当前时间。</param>
    /// <param name = "value">字段值；不可用时为null。</param>
    /// <param name = "reason">不可用原因；可用时为空字符串。</param>
    public static bool TryGetTaskValue(
        InspectionRequest request,
        string key,
        DateTimeOffset now,
        out string? value,
        out string reason
    )
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        value = null;
        var data = request.TaskData;
        if (data == null)
        {
            reason = "未提供本周期任务引导数据。";
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(request.CycleId)
            || !string.Equals(request.CycleId, data.CycleId, StringComparison.Ordinal)
        )
        {
            reason = "图像与引导数据的周期不一致。";
            return false;
        }

        if (now < data.CapturedAt || now > data.ValidUntil)
        {
            reason = "引导数据尚未生效或已经过期。";
            return false;
        }

        if (!data.Values.TryGetValue(key, out value))
        {
            reason = "缺少任务引导字段：" + key;
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// 按ROI字段设置检查实际读数：配置的引导值（不一致时另逐位列出同长度下的字符差异）、长度、允许字符和整段格式。
    /// </summary>
    /// <param name = "text">实际读数。</param>
    /// <param name = "field">ROI字段设置。</param>
    /// <param name = "bounds">发现所附的原图范围（当前ROI）。</param>
    /// <returns>不符合项（NG）；全部符合时为空。</returns>
    public static IReadOnlyList<InspectionFinding> CheckRules(
        string text,
        FieldSettings field,
        PixelRect bounds
    )
    {
        if (text == null || field == null)
        {
            throw new ArgumentNullException(text == null ? nameof(text) : nameof(field));
        }

        var findings = new List<InspectionFinding>();
        void Fail(string code, string message)
        {
            findings.Add(new InspectionFinding(code, message, EInspectionVerdict.Ng, bounds));
        }

        if (field.Expected != null && !string.Equals(text, field.Expected, StringComparison.Ordinal))
        {
            Fail("content_mismatch", $"实际=[{text}]，引导值=[{field.Expected}]；原始读数未修改。");
            if (text.Length == field.Expected.Length)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != field.Expected[i])
                    {
                        Fail(
                            "content_character_mismatch",
                            $"文本偏移{i}：实际=[{text[i]}]，引导=[{field.Expected[i]}]。这是原始字符串位置，不是物理字符缺陷框。"
                        );
                    }
                }
            }
        }

        if (text.Length < field.MinimumLength || text.Length > field.MaximumLength)
        {
            Fail("length_mismatch", "实际数据长度不符合配置。");
        }

        if (field.AllowedCharacters != null && text.Any(c => !field.AllowedCharacters.Contains(c)))
        {
            Fail("charset_mismatch", "实际数据包含不允许字符。");
        }

        if (
            field.Pattern != null
            && !Regex.IsMatch(
                text,
                "\\A(?:" + field.Pattern + ")\\z",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            )
        )
        {
            Fail("pattern_mismatch", "实际数据不符合整段格式。");
        }

        return findings;
    }

    /// <summary>实际读数与每个引导值（其他ROI的实际读数或任务字段）逐字符精确比较。</summary>
    /// <param name = "text">实际读数。</param>
    /// <param name = "guides">引导值，按绑定顺序。</param>
    /// <param name = "bounds">发现所附的原图范围（当前ROI）。</param>
    /// <returns>每个不一致的引导值一条 <c>binding_mismatch</c>（NG）。</returns>
    public static IReadOnlyList<InspectionFinding> CompareGuides(
        string text,
        IEnumerable<string> guides,
        PixelRect bounds
    )
    {
        if (text == null || guides == null)
        {
            throw new ArgumentNullException(text == null ? nameof(text) : nameof(guides));
        }

        return guides
            .Where(guide => !string.Equals(text, guide, StringComparison.Ordinal))
            .Select(guide => new InspectionFinding(
                "binding_mismatch",
                $"实际=[{text}]，引导值=[{guide}]；未执行后续质量检查。",
                EInspectionVerdict.Ng,
                bounds
            ))
            .ToList();
    }
}
