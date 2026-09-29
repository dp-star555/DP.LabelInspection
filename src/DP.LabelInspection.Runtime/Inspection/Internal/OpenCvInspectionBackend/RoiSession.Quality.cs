using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

public sealed partial class OpenCvInspectionBackend
{
    private sealed partial class RoiSession
    {
        /// <summary>字库缺失或未绑定时的空参考集合；参考集合本身是借用的，不需要释放。</summary>
        private static readonly IReadOnlyDictionary<string, A.GlyphTemplate> NoReferences =
            new Dictionary<string, A.GlyphTemplate>(StringComparer.Ordinal);

        /// <summary>执行当前ROI的质量策略，保留已有实际读取及独立完成状态。</summary>
        /// <param name = "r">已定位的ROI及质量配置。</param>
        /// <param name = "reading">本轮已有真实读取证据，质量返回不能覆盖它。</param>
        /// <param name = "token">协作式取消标记。</param>
        public RoiQualityMeasurement InspectQuality(
            InspectionRegion r,
            RegionInspectionResult reading,
            CancellationToken token
        )
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (r.Kind == ERegionKind.Text)
            {
                return TextQuality(r, reading, token);
            }

            if (r.Kind == ERegionKind.Barcode)
            {
                using var source = _request.VisionSource!.Retain();
                bool qr = r.Field.BarcodeType == EBarcodeKind.QrCode
                    || r.Field.BarcodeType == EBarcodeKind.Auto
                        && reading.Barcodes.Count == 1 && reading.Barcodes[0].Format == "QR_CODE";
                var result = qr
                    ? _owner._qrQuality.Inspect(source, r.Bounds,
                        reading.Barcodes.Select(Bridge.ToVision).ToArray(),
                        r.Field.BarcodePrint, token)
                    : _owner._linearQuality.Inspect(source, r.Bounds,
                        reading.Barcodes.Select(Bridge.ToVision).ToArray(),
                        r.Field.BarcodePrint, token);
                return new RoiQualityMeasurement(
                    new RegionInspectionResult(r.Name, result.Findings.Select(Bridge.ToLabel),
                        barcodes: reading.Barcodes),
                    result.Status == A.EAlgorithmStatus.Completed
                );
            }

            using var actual = _request.VisionSource!.Crop(
                r.Bounds.X,
                r.Bounds.Y,
                r.Bounds.Width,
                r.Bounds.Height
            );
            // 忽略区随定位平移后与本ROI的交集，在ROI局部坐标中扣除。
            var local = new A.PixelBounds(0, 0, r.Bounds.Width, r.Bounds.Height);
            var ignored = _request
                .Recipe.Regions.Where(o => o.Kind == ERegionKind.Ignore)
                .Select(o =>
                    new A.PixelBounds(
                        o.Bounds.X + OffsetX - r.Bounds.X,
                        o.Bounds.Y + OffsetY - r.Bounds.Y,
                        o.Bounds.Width,
                        o.Bounds.Height
                    ).Intersect(local)
                )
                .Where(b => b != null)
                .Select(b => (Geometry)b!.Value.ToGeometry());
            using var mask = A.InspectionMask.ToImage(
                A.InspectionMask.Compose(actual, Array.Empty<Geometry>(), ignored, token),
                local,
                token
            );
            var options = _request.Recipe.Options;
            var parameters = new A.InkInspectionOptions(
                options.InkThreshold,
                options.TolerancePixels,
                options.MinimumDefectArea
            );
            var origin = new PointD(r.Bounds.X, r.Bounds.Y);
            A.InkInspectionResult measured;
            if (r.Kind == ERegionKind.Blank)
            {
                measured = _owner._qualityAlgorithms.Blank.Inspect(actual, origin, parameters, mask, token);
            }
            else
            {
                var original = _request.Recipe.Regions.Single(o => o.Name == r.Name);
                using var reference =
                    _request.VisionReference?.Crop(
                        original.Bounds.X,
                        original.Bounds.Y,
                        original.Bounds.Width,
                        original.Bounds.Height
                    )
                    ?? throw new InvalidOperationException(
                        "fixed_quality_requires_reference: 规则质检需要参考图。"
                    );
                measured = _owner._qualityAlgorithms.Fixed.Inspect(
                    actual,
                    reference,
                    origin,
                    parameters,
                    mask,
                    token
                );
            }

            var output = measured
                .Defects.Select(d => new InspectionFinding(
                    d.Code,
                    $"{d.Code}: {d.Area}原图像素²",
                    EInspectionVerdict.Ng,
                    LegacyBounds(d.Bounds, r.Bounds),
                    d.Area
                ))
                .ToList();
            if (measured.Status != A.EAlgorithmStatus.Completed)
            {
                output.Add(
                    new InspectionFinding(
                        measured.ReasonCode,
                        measured.Reason,
                        EInspectionVerdict.Ng,
                        r.Bounds
                    )
                );
            }

            return new RoiQualityMeasurement(
                new RegionInspectionResult(r.Name, output),
                measured.Status == A.EAlgorithmStatus.Completed
            );
        }

        private static string GlyphStatusText(string status)
        {
            switch (status)
            {
                case "compared":
                    return "通过";
                case "exceeds_threshold":
                    return "印刷差异超过阈值";
                case "missing_template":
                    return "单字库缺少该字符的参考";
                default:
                    return "未能比较（" + status + "）";
            }
        }

        /// <summary>
        /// 逐字汇总：字符假设来源、分割依据及每个字的差异/缺墨/多墨，便于在OK时也能看到余量。
        /// 汇总只作说明，判定仍由逐字结果和分割状态决定。
        /// </summary>
        private static string TextQualitySummary(
            InspectionRegion r,
            string hypothesis,
            CharacterSegmentation? segmentation,
            IReadOnlyList<GlyphInspection> glyphs
        )
        {
            string source =
                r.Field.EqualCells ? "引导值（等宽单元）"
                : r.Field.Expected != null && r.Field.Expected == hypothesis ? "OCR读数（已与引导值一致）"
                : "OCR读数（盲测假设，未经业务真值确认）";
            var parts = glyphs.Select(g =>
                g.Comparison == null
                    ? $"{g.Character.Character}:{GlyphStatusText(g.Status)}"
                    : $"{g.Character.Character}:{g.Comparison.Difference:P1}/缺{g.Comparison.Missing}/多{g.Comparison.Extra}"
                        + (g.Status == "compared" ? "" : "✗")
            );
            int failed = glyphs.Count(g => g.Status != "compared");
            return $"字符假设=[{hypothesis}]，来源：{source}；分割依据={segmentation?.Basis ?? "-"}；"
                + $"{glyphs.Count}字中{failed}字未通过，差异阈值{r.Field.MaximumDifference:P2}。逐字（差异/内部缺墨px/多墨px）："
                + string.Join("  ", parts);
        }

        private RoiQualityMeasurement TextQuality(
            InspectionRegion r,
            RegionInspectionResult reading,
            CancellationToken token
        )
        {
            GlyphReferenceLease? lease = null;
            try
            {
                _libraries.TryGetValue(r.Name, out var library);
                // 固定版本的参考图在缓存里只转换一次；本ROI只取一份独立租约。
                // 缓存即使同时逐出该版本，也只归还它自己那份，本次比较读到的像素仍然有效。
                lease = library == null ? null : _owner._referenceImages.Acquire(library);
                var references = lease?.Templates ?? NoReferences;

                using var actual = _request.VisionSource!.Retain();
                var strategy =
                    _owner._textQuality
                    ?? new A.TextQualityInspector(
                        _owner._segmenter,
                        _owner._matcher,
                        _owner._comparer
                    );
                string hypothesis = r.Field.EqualCells ? r.Field.Expected! : reading.Recognition?.Text ?? "";
                using var measured = strategy.Inspect(
                    new A.TextQualityRequest(
                        actual,
                        r.Bounds,
                        hypothesis,
                        r.Field.EqualCells,
                        references,
                        _request.Recipe.Options.InkThreshold,
                        r.Field.GlyphTolerance,
                        r.Field.MaximumDifference
                    ),
                    token
                );
                var segmentation =
                    measured.Segmentation == null ? null : Bridge.ToLabel(measured.Segmentation);
                var glyphs = new List<GlyphInspection>();
                var findings = measured.Findings.Select(Bridge.ToLabel).ToList();
                if (
                    measured.Segmentation != null
                    && measured.Segmentation.Status != "provisional"
                    && measured.Segmentation.Status != "explicit_cells"
                )
                {
                    findings.Add(
                        new InspectionFinding(
                            "segmentation_review",
                            measured.Segmentation.Reason,
                            EInspectionVerdict.Ng,
                            r.Bounds
                        )
                    );
                }

                foreach (var g in measured.Glyphs)
                {
                    var character = new CharacterPatch(
                        g.Character.Character,
                        g.Character.TokenIndex,
                        g.Character.Bounds,
                        Bridge.ToLabel(g.Character.Patch),
                        g.Character.NeighborInkRemoved
                    );
                    GlyphComparison? comparison = null;
                    var c = g.Comparison;
                    if (c?.Actual != null && c.Reference != null && c.Delta != null)
                    {
                        comparison = new GlyphComparison(
                            c.Status == A.EAlgorithmStatus.Completed ? "compared" : c.ReasonCode,
                            c.Difference,
                            c.Missing,
                            c.Extra,
                            Bridge.ToLabel(c.Actual),
                            Bridge.ToLabel(c.Reference),
                            Bridge.ToLabel(c.Delta)
                        );
                    }

                    string? hash =
                        g.ReferenceKey != null
                        && library != null
                        && library.Glyphs.TryGetValue(g.ReferenceKey, out var reference)
                            ? reference.Sha256
                            : null;
                    glyphs.Add(new GlyphInspection(character, g.Status, hash, comparison));
                    if (g.Status != "compared")
                    {
                        findings.Add(
                            new InspectionFinding(
                                g.Status == "missing_template" ? "missing_template" : "glyph_" + g.Status,
                                $"字符[{character.Character}]（第{character.TokenIndex + 1}位）：{GlyphStatusText(g.Status)}"
                                    + (
                                        comparison == null
                                            ? ""
                                            : $"；差异{comparison.Difference:P2}（阈值{r.Field.MaximumDifference:P2}），内部缺墨{comparison.Missing}px、多墨{comparison.Extra}px"
                                    ),
                                EInspectionVerdict.Ng,
                                character.Bounds
                            )
                        );
                    }
                }

                if (glyphs.Count > 0)
                {
                    findings.Add(
                        new InspectionFinding(
                            "text_quality_summary",
                            TextQualitySummary(r, hypothesis, segmentation, glyphs),
                            EInspectionVerdict.Ok,
                            r.Bounds
                        )
                    );
                }

                return new RoiQualityMeasurement(
                    new RegionInspectionResult(r.Name, findings, reading.Recognition, segmentation, glyphs),
                    measured.Completed
                );
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }
}
