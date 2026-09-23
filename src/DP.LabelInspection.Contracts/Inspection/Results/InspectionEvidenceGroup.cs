using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>一个带编号的ROI展示/输出项，每条原始观测均保留为不可变子项。</summary>
public sealed class InspectionEvidenceGroup
{
    internal InspectionEvidenceGroup(
        string id,
        string regionName,
        bool isBarcode,
        InspectionFinding summary,
        IEnumerable<InspectionFinding> children
    )
        : this(
            id,
            regionName,
            isBarcode,
            summary,
            children.Select((finding, index) => new InspectionEvidenceDetail(id + "." + (index + 1), finding))
        ) { }

    /// <summary>复制创建可移植序列化分组，子项标识仅在本父项内有效。</summary>
    /// <param name = "id">本报告内的父项标识，例如F1。</param>
    /// <param name = "regionName">原始ROI名称，全局证据可为空。</param>
    /// <param name = "isBarcode">是否属于条码或QR分组。</param>
    /// <param name = "summary">用于父项显示的综合证据，不替换原始子项。</param>
    /// <param name = "children">完整子证据集合，内部复制，子项编号在父项内有效。</param>
    public InspectionEvidenceGroup(
        string id,
        string regionName,
        bool isBarcode,
        InspectionFinding summary,
        IEnumerable<InspectionEvidenceDetail> children
    )
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Group ID is required.", nameof(id));
        }

        var copy = children?.ToArray() ?? throw new ArgumentNullException(nameof(children));
        if (copy.Any(c => c == null) || copy.Where((c, i) => c.Id != id + "." + (i + 1)).Any())
        {
            throw new ArgumentException("Invalid child IDs.", nameof(children));
        }

        Id = id;
        RegionName = regionName ?? throw new ArgumentNullException(nameof(regionName));
        IsBarcode = isBarcode;
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Children = Array.AsReadOnly(copy);
    }

    /// <summary>在本份完整报告内稳定，例如F1，不作为跨运行标识。</summary>
    public string Id { get; }

    /// <summary>原始区域名称，全局证据为空。</summary>
    public string RegionName { get; }

    /// <summary>是否为条码或QR的分组结果。</summary>
    public bool IsBarcode { get; }

    /// <summary>显示严重程度及原图范围，优先级为NG高于REVIEW高于OK。</summary>
    public InspectionFinding Summary { get; }

    /// <summary>供人阅读的导出状态：OK通过、NG不通过或REVIEW待复核。</summary>
    public string Status => Summary.Verdict.ToString().ToUpperInvariant();

    /// <summary>去重后的局部候选框数量，不是经认证的物理缺陷数；缺参考和分割失败不计入。</summary>
    public int LocalizedCandidateCount =>
        Children
            .Where(c => c.IsLocalizedCandidate && c.Finding.Bounds.HasValue)
            .Select(c => c.Finding.Bounds!.Value)
            .Distinct()
            .Count();

    /// <summary>阻止完成的诊断记录，不是局部缺陷点。</summary>
    public int BlockingItemCount => Children.Count(c => c.IsComparisonBlocker);

    /// <summary>全部原始发现，包含单个缺陷、内容约束及不确定提示。</summary>
    public IReadOnlyList<InspectionEvidenceDetail> Children { get; }

    private static string Trace(RoiExecution? execution)
    {
        if (execution == null)
        {
            return "";
        }

        string State(ERoiStageState state)
        {
            return state == ERoiStageState.Passed ? "通过"
                : state == ERoiStageState.Failed ? "失败"
                : state == ERoiStageState.NotRequested ? "未配置/不适用"
                : "未执行";
        }

        return "前检="
            + State(execution.Prerequisites)
            + "；读取="
            + State(execution.Data)
            + "；引导值="
            + State(execution.Comparison)
            + "；质量="
            + State(execution.Quality)
            + "。";
    }

    internal static IReadOnlyList<InspectionEvidenceGroup> Create(
        BackendAnalysis analysis,
        IEnumerable<InspectionFinding> global
    )
    {
        var groups = new List<InspectionEvidenceGroup>();
        foreach (var region in analysis.Regions)
        {
            bool barcode =
                region.Barcodes.Count > 0
                || region.Findings.Any(f =>
                    f.Code.StartsWith("barcode_", StringComparison.Ordinal)
                    || f.Code.StartsWith("qr_", StringComparison.Ordinal)
                );
            if (barcode)
            {
                var verdict =
                    region.Findings.Any(f => f.Verdict == EInspectionVerdict.Ng) ? EInspectionVerdict.Ng
                    : region.Findings.Any(f => f.Verdict == EInspectionVerdict.Review)
                    || region.Findings.Count == 0
                        ? EInspectionVerdict.Review
                    : EInspectionVerdict.Ok;
                var boxes = region
                    .Findings.Where(f => f.Bounds.HasValue)
                    .Select(f => f.Bounds!.Value)
                    .Concat(region.Barcodes.Select(b => b.Bounds))
                    .ToArray();
                PixelRect? bounds = null;
                if (boxes.Length > 0)
                {
                    int x = boxes.Min(b => b.X),
                        y = boxes.Min(b => b.Y);
                    bounds = new PixelRect(
                        x,
                        y,
                        boxes.Max(b => b.X + b.Width) - x,
                        boxes.Max(b => b.Y + b.Height) - y
                    );
                }

                int ng = region.Findings.Count(f => f.Verdict == EInspectionVerdict.Ng),
                    review = region.Findings.Count(f => f.Verdict == EInspectionVerdict.Review);
                string message =
                    $"条码汇总：{ng}项NG，{review}项REVIEW；{region.Findings.Count}项明细完整保留。"
                    + string.Join("；", region.Barcodes.Select(b => b.Format + ": " + b.Text));
                groups.Add(
                    new InspectionEvidenceGroup(
                        "F" + (groups.Count + 1),
                        region.RegionName,
                        true,
                        new InspectionFinding(
                            "barcode_summary",
                            Trace(region.Execution) + message,
                            verdict,
                            bounds
                        ),
                        region.Findings
                    )
                );
            }
            else
            {
                var verdict =
                    region.Findings.Any(f => f.Verdict == EInspectionVerdict.Ng) ? EInspectionVerdict.Ng
                    : region.Findings.Any(f => f.Verdict == EInspectionVerdict.Review)
                        ? EInspectionVerdict.Review
                    : region.Findings.Count > 0 || region.Glyphs.Count > 0 ? EInspectionVerdict.Ok
                    : EInspectionVerdict.Review;
                var boxes = region
                    .Findings.Where(f => f.Bounds.HasValue)
                    .Select(f => f.Bounds!.Value)
                    .Concat(region.Glyphs.Select(g => g.Character.Bounds))
                    .Concat(
                        region.Recognition == null
                            ? Array.Empty<PixelRect>()
                            : new[] { region.Recognition.Bounds }
                    )
                    .ToArray();
                PixelRect? bounds = null;
                if (boxes.Length > 0)
                {
                    int x = boxes.Min(b => b.X),
                        y = boxes.Min(b => b.Y);
                    bounds = new PixelRect(
                        x,
                        y,
                        boxes.Max(b => b.X + b.Width) - x,
                        boxes.Max(b => b.Y + b.Height) - y
                    );
                }

                string id = "F" + (groups.Count + 1);
                var children = region
                    .Findings.Select((f, i) => new InspectionEvidenceDetail(id + "." + (i + 1), f))
                    .ToArray();
                int points = children
                        .Where(c => c.IsLocalizedCandidate && c.Finding.Bounds.HasValue)
                        .Select(c => c.Finding.Bounds!.Value)
                        .Distinct()
                        .Count(),
                    blocks = children.Count(c => c.IsComparisonBlocker);
                string message =
                    $"ROI汇总：{points}个定位候选框，{blocks}条比对阻断记录；{region.Findings.Count(f => f.Verdict == EInspectionVerdict.Ng)}项NG，{region.Findings.Count(f => f.Verdict == EInspectionVerdict.Review)}项待复核。明细{children.Length}项完整保留；阻断记录不是缺陷点。";
                groups.Add(
                    new InspectionEvidenceGroup(
                        id,
                        region.RegionName,
                        false,
                        new InspectionFinding(
                            "roi_summary",
                            Trace(region.Execution) + message,
                            verdict,
                            bounds
                        ),
                        children
                    )
                );
            }
        }

        foreach (var finding in global)
        {
            groups.Add(
                new InspectionEvidenceGroup(
                    "F" + (groups.Count + 1),
                    "",
                    false,
                    finding,
                    Array.Empty<InspectionFinding>()
                )
            );
        }

        return groups.AsReadOnly();
    }
}
