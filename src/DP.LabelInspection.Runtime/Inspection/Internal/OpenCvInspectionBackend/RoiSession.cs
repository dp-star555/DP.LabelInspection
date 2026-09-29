using System;
using System.Collections.Concurrent;
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
    private sealed partial class RoiSession : IConcurrentRoiSession, IRoiAnomalySession
    {
        private readonly OpenCvInspectionBackend _owner;
        private readonly InspectionRequest _request;
        // 各ROI只写入自己名称下的条目；多个ROI可并发执行（IConcurrentRoiSession）。
        private readonly ConcurrentDictionary<string, GlyphLibrarySnapshot> _libraries =
            new ConcurrentDictionary<string, GlyphLibrarySnapshot>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<
            string,
            (AnomalyModelEntry Entry, A.PatchAnomalyModel Model, A.IPatchAnomalyDetector Detector)
        > _anomaly = new ConcurrentDictionary<
            string,
            (AnomalyModelEntry, A.PatchAnomalyModel, A.IPatchAnomalyDetector)
        >(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Dictionary<string, CharacterAnomalyModel>> _characterModels =
            new ConcurrentDictionary<string, Dictionary<string, CharacterAnomalyModel>>(StringComparer.Ordinal);
        private readonly object _locateGate = new object();
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
                    && (r.Field.BarcodeType == EBarcodeKind.Auto
                        || (r.Field.BarcodeType == EBarcodeKind.QrCode
                            ? _owner._qrQuality.RequiresDecodedStructure
                            : _owner._linearQuality.RequiresDecodedStructure));
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

            if (
                (long)r.Bounds.X + r.Bounds.Width > _request.ImageWidth
                || (long)r.Bounds.Y + r.Bounds.Height > _request.ImageHeight
            )
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
                    !_request.HasReference
                    || _request.ReferenceWidth != _request.ImageWidth
                    || _request.ReferenceHeight != _request.ImageHeight
                )
            )
            {
                Fail(
                    "missing_reference",
                    "该ROI或定位需要同尺寸整图参考；待检 "
                        + _request.ImageWidth
                        + "×"
                        + _request.ImageHeight
                        + "，参考 "
                        + (
                            !_request.HasReference
                                ? "未载入"
                                : _request.ReferenceWidth + "×" + _request.ReferenceHeight
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

            if (
                readRequired
                && r.Kind == ERegionKind.Barcode
                && _owner._barcode == null
            )
            {
                Fail("barcode_unavailable", "需要读码数据/结构，但宿主未提供读码实现。");
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
                    // 固定版本不可变：同一后台内按“库+版本”缓存，不再每次检测都从存储重新载入。
                    var libraries = _owner._libraries;
                    string id = r.Field.LibraryId;
                    int revision = r.Field.LibraryRevision!.Value;
                    var library = _owner._glyphLibraries.Get(id, revision, () => libraries.Load(id, revision));
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

        /// <summary>读取实际OCR或码数据，不在此进行质量批准。</summary>
        /// <param name = "r">已通过前提并定位的ROI。</param>
        /// <param name = "token">协作式取消标记。</param>
        public RegionInspectionResult Read(InspectionRegion r, CancellationToken token)
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (r.Kind == ERegionKind.Text)
            {
                using var source = _request.VisionSource!.Retain();
                return new RegionInspectionResult(
                    r.Name,
                    Array.Empty<InspectionFinding>(),
                    recognition: _owner._recognizer!.Recognize(source, r.Bounds, token)
                );
            }

            if (r.Kind == ERegionKind.Barcode)
            {
                using var source = _request.VisionSource!.Retain();
                var measured = _owner._barcode!.Read(source, r.Bounds, token);
                return new RegionInspectionResult(
                    r.Name,
                    Array.Empty<InspectionFinding>(),
                    barcodes: measured.Observations.Select(Bridge.ToLabel)
                );
            }

            throw new InvalidOperationException("ROI has no reading operation.");
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
            _anomaly.Clear();
            _characterModels.Clear();
        }
    }
}
