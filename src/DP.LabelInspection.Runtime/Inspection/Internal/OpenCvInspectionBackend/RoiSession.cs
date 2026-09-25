using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision;
using OpenCvSharp;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

public sealed partial class OpenCvInspectionBackend
{
    private sealed class RoiSession : IRoiInspectionSession
    {
        private readonly OpenCvInspectionBackend _owner;
        private readonly InspectionRequest _request;
        private readonly Dictionary<string, GlyphLibrarySnapshot> _libraries = new Dictionary<
            string,
            GlyphLibrarySnapshot
        >(StringComparer.Ordinal);
        private bool _located,
            _alignmentFailed,
            _disposed;

        internal RoiSession(OpenCvInspectionBackend owner, InspectionRequest request)
        {
            _owner = owner;
            _request = request;
        }

        public int OffsetX { get; private set; }
        public int OffsetY { get; private set; }

        /// <summary>查询当前质量策略的读取依赖，不执行识别。</summary>
        /// <param name = "r">当前ROI及其所选项目和质量策略。</param>
        public bool QualityNeedsReading(InspectionRegion r)
        {
            return r.Kind == ERegionKind.Text
                ? !r.Field.EqualCells && (_owner._textQuality?.RequiresRecognition ?? true)
                : r.Kind == ERegionKind.Barcode
                    && (
                        _owner._barcodePrint is IBarcodePrintRequirements requirements
                            ? requirements.RequiresReading(r.Field.BarcodeType)
                            : true
                    );
        }

        /// <summary>在昂贵算法前检查配置、能力及资源，返回明确前提证据。</summary>
        /// <param name = "r">当前ROI及其配置。</param>
        /// <param name = "readRequired">是否需要真实读取，已计入质量依赖。</param>
        /// <param name = "qualityRequired">是否要求完整执行质量检查。</param>
        /// <param name = "token">协作式取消标记。</param>
        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion r,
            bool readRequired,
            bool qualityRequired,
            CancellationToken token
        )
        {
            Alive();
            token.ThrowIfCancellationRequested();
            var findings = new List<InspectionFinding>();
            void Fail(string code, string reason)
            {
                findings.Add(new InspectionFinding(code, reason, EInspectionVerdict.Ng, r.Bounds));
            }

            if (!r.Bounds.Fits(_request.Actual))
            {
                Fail("roi_outside_image", "ROI超出原图。");
            }

            foreach (
                var other in _request.Recipe.Regions.Where(o =>
                    o.Name != r.Name && o.Kind != ERegionKind.Ignore && o.Bounds.Intersects(r.Bounds)
                )
            )
            {
                if (
                    !(
                        (r.Kind == ERegionKind.Text && other.Kind == ERegionKind.Barcode)
                        || (r.Kind == ERegionKind.Barcode && other.Kind == ERegionKind.Text)
                    )
                )
                {
                    Fail("roi_overlap", "ROI与其他检测区冲突：" + other.Name);
                }
            }

            if (
                (r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode)
                && _request.Recipe.Regions.Any(o =>
                    o.Kind == ERegionKind.Ignore && o.Bounds.Intersects(r.Bounds)
                )
            )
            {
                Fail("ignored_read_scope", "忽略区与文字/码区相交，不在改变后的证据上读取或质检。");
            }

            bool referenceNeeded =
                qualityRequired && r.Kind == ERegionKind.Fixed
                || _request.Recipe.Mode == EInspectionMode.Template
                    && _request.Recipe.Alignment == EAlignmentMode.Translation;
            if (
                referenceNeeded
                && (
                    _request.Reference == null
                    || _request.Reference.Width != _request.Actual.Width
                    || _request.Reference.Height != _request.Actual.Height
                )
            )
            {
                Fail(
                    "missing_reference",
                    "该ROI或定位需要同尺寸整图参考；待检 "
                        + _request.Actual.Width
                        + "×"
                        + _request.Actual.Height
                        + "，参考 "
                        + (
                            _request.Reference == null
                                ? "未载入"
                                : _request.Reference.Width + "×" + _request.Reference.Height
                        )
                        + "。仅单字库质检不需要整图参考。"
                );
            }

            if (readRequired && r.Kind == ERegionKind.Text)
            {
                if (_owner._recognizer == null)
                {
                    Fail("ocr_unavailable", "需要OCR数据/身份，但宿主未提供OCR实现。");
                }

                if (!r.SingleLine)
                {
                    Fail("text_layout_required", "当前OCR要求明确框选横向单行。");
                }
            }

            if (readRequired && r.Kind == ERegionKind.Barcode && _owner._barcode == null)
            {
                Fail("barcode_unavailable", "需要读码数据/结构，但宿主未提供读码实现。");
            }

            if (qualityRequired && r.Kind == ERegionKind.Barcode)
            {
                if (!(_owner._barcodePrint is IRoiBarcodeQualityInspector))
                {
                    Fail(
                        "quality_completion_unavailable",
                        "码质检实现未提供明确完成状态，不能把空列表当成功。"
                    );
                }
            }

            if (
                qualityRequired
                && r.Kind == ERegionKind.Text
                && (_owner._textQuality?.RequiresReferences ?? true)
            )
            {
                if (r.Field.LibraryId == null)
                {
                    Fail("glyph_library_unbound", "文字质量要求参考字库或替代质量策略，当前未绑定字库。");
                }
                else if (_owner._libraries == null)
                {
                    Fail("glyph_repository_unavailable", "没有可用的参考字库提供者。");
                }
                else
                {
                    var library = _owner._libraries.Load(r.Field.LibraryId, r.Field.LibraryRevision!.Value);
                    if (library.Id != r.Field.LibraryId || library.Revision != r.Field.LibraryRevision)
                    {
                        throw new InvalidOperationException(
                            "Reference provider substituted the pinned revision."
                        );
                    }

                    _libraries[r.Name] = library;
                    if (r.Field.Expected != null)
                    {
                        foreach (var c in r.Field.Expected.Where(FieldSettings.IsAlphanumeric).Distinct())
                        {
                            if (!library.Glyphs.ContainsKey(c.ToString()))
                            {
                                Fail("missing_template", "已知必需字符缺少参考：" + c);
                            }
                        }
                    }
                }
            }

            return findings;
        }

        /// <summary>按配置执行定位，返回实际原图坐标中的ROI。</summary>
        /// <param name = "r">定位前的ROI快照。</param>
        /// <param name = "token">协作式取消标记。</param>
        public InspectionRegion Locate(InspectionRegion r, CancellationToken token)
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (
                _request.Recipe.Mode == EInspectionMode.Template
                && _request.Recipe.Alignment == EAlignmentMode.Translation
                && !_located
            )
            {
                _located = true;
                using var a = Gray(_request.Actual);
                using var reference = Gray(_request.Reference!);
                var offset = TranslationRegistration.Translation(a, reference, _request.Recipe);
                if (offset == null)
                {
                    _alignmentFailed = true;
                }
                else
                {
                    OffsetX = offset.Item1;
                    OffsetY = offset.Item2;
                }
            }

            if (_alignmentFailed)
            {
                throw new InvalidOperationException("alignment_failed: 未取得可靠定位。");
            }

            long x = (long)r.Bounds.X + OffsetX,
                y = (long)r.Bounds.Y + OffsetY;
            if (
                x < 0
                || y < 0
                || x + r.Bounds.Width > _request.Actual.Width
                || y + r.Bounds.Height > _request.Actual.Height
            )
            {
                throw new InvalidOperationException("定位后ROI超出原图。");
            }

            return new InspectionRegion(
                r.Name,
                r.Kind,
                new PixelRect((int)x, (int)y, r.Bounds.Width, r.Bounds.Height),
                r.SingleLine,
                r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode ? r.Field : null
            ).WithTasks(r.Tasks);
        }

        /// <summary>读取实际OCR或码数据，不在此进行质量批准。</summary>
        /// <param name = "r">已通过前提并定位的ROI。</param>
        /// <param name = "token">协作式取消标记。</param>
        public RegionInspectionResult Read(InspectionRegion r, CancellationToken token)
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (r.Kind == ERegionKind.Text)
            {
                return new RegionInspectionResult(
                    r.Name,
                    Array.Empty<InspectionFinding>(),
                    recognition: _owner._recognizer!.Recognize(_request.Actual, r.Bounds, token)
                );
            }

            if (r.Kind == ERegionKind.Barcode)
            {
                return new RegionInspectionResult(
                    r.Name,
                    Array.Empty<InspectionFinding>(),
                    barcodes: _owner._barcode!.Decode(_request.Actual, r.Bounds, token)
                );
            }

            throw new InvalidOperationException("ROI has no reading operation.");
        }

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
                return ((IRoiBarcodeQualityInspector)_owner._barcodePrint).InspectQuality(
                    _request.Actual,
                    r,
                    reading.Barcodes,
                    token
                );
            }

            using var actual = Bridge.ToVision(_request.Actual.Crop(r.Bounds));
            var bytes = Enumerable.Repeat((byte)255, r.Bounds.Width * r.Bounds.Height).ToArray();
            foreach (var ignored in _request.Recipe.Regions.Where(o => o.Kind == ERegionKind.Ignore))
            {
                int left = Math.Max(r.Bounds.X, ignored.Bounds.X + OffsetX),
                    right = Math.Min(
                        r.Bounds.X + r.Bounds.Width,
                        ignored.Bounds.X + OffsetX + ignored.Bounds.Width
                    );
                int top = Math.Max(r.Bounds.Y, ignored.Bounds.Y + OffsetY),
                    bottom = Math.Min(
                        r.Bounds.Y + r.Bounds.Height,
                        ignored.Bounds.Y + OffsetY + ignored.Bounds.Height
                    );
                for (int y = top; y < bottom; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = left; x < right; x++)
                    {
                        bytes[(y - r.Bounds.Y) * r.Bounds.Width + x - r.Bounds.X] = 0;
                    }
                }
            }

            using var mask = VisionImage.CopyFrom(
                new ImageInfo(r.Bounds.Width, r.Bounds.Height, EPixelLayout.Gray8),
                bytes
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
                using var reference = Bridge.ToVision(_request.Reference!.Crop(original.Bounds));
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
            var owned = new List<IImageSource>();
            try
            {
                _libraries.TryGetValue(r.Name, out var library);
                var references = new Dictionary<string, A.GlyphTemplate>(StringComparer.Ordinal);
                var legacy = new Dictionary<IImageSource, GlyphReference>();
                if (library != null)
                {
                    foreach (var entry in library.Glyphs)
                    {
                        var image = Bridge.ToVision(entry.Value.Image);
                        owned.Add(image);
                        references.Add(
                            entry.Key,
                            new A.GlyphTemplate(
                                image,
                                entry.Value.Binarization == "otsu"
                                    ? A.EGlyphBinarization.Otsu
                                    : A.EGlyphBinarization.Fixed
                            )
                        );
                        legacy[image] = entry.Value;
                    }
                }

                using var actual = Bridge.ToVision(_request.Actual);
                var strategy =
                    _owner._textQuality
                    ?? new A.TextQualityInspector(
                        _owner._segmenter is CharacterSegmenter segmenter
                            ? segmenter.Algorithm
                            : new LegacySegmenter(_owner._segmenter),
                        _owner._matcher,
                        _owner._comparer is GlyphComparer comparer
                            ? comparer.Algorithm
                            : new LegacyComparer(_owner._comparer, legacy)
                    );
                string hypothesis = r.Field.EqualCells ? r.Field.Expected! : reading.Recognition?.Text ?? "";
                using var measured = strategy.Inspect(
                    new A.TextQualityRequest(
                        actual,
                        Bridge.ToVision(r.Bounds),
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
                        Bridge.ToLabel(g.Character.Bounds),
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
                foreach (var image in owned)
                {
                    image.Dispose();
                }
            }
        }

        private void Alive()
        {
            if (_disposed || _owner._disposed)
            {
                throw new ObjectDisposedException(nameof(RoiSession));
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _libraries.Clear();
        }
    }
}
