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
        /// <summary>检查方法B的模型绑定、固定版本、模型键、特征实现及位置相关模型的裁图尺寸，并缓存解析后的模型。</summary>
        /// <param name = "r">当前ROI配置（配方坐标）。</param>
        /// <param name = "token">协作式取消标记。</param>
        public IReadOnlyList<InspectionFinding> ValidateAnomaly(InspectionRegion r, CancellationToken token)
        {
            Alive();
            token.ThrowIfCancellationRequested();
            var findings = new List<InspectionFinding>();
            void Fail(string code, string reason)
            {
                findings.Add(new InspectionFinding(code, reason, EInspectionVerdict.Ng, r.Bounds));
            }

            var pin = r.Anomaly;
            if (pin == null)
            {
                Fail("anomaly_model_unbound", "选择了B异常检测，但ROI未绑定异常模型库。");
                return findings;
            }

            if (_owner._anomalyModels == null)
            {
                Fail("anomaly_repository_unavailable", "没有可用的异常模型库提供者。");
                return findings;
            }

            AnomalyLibrarySnapshot library;
            try
            {
                library = _owner._anomalyCache.Library(
                    _owner._anomalyModels,
                    pin.LibraryId,
                    pin.LibraryRevision
                );
            }
            catch (Exception error)
                when (error is System.IO.IOException
                    || error is System.IO.InvalidDataException
                    || error is UnauthorizedAccessException
                )
            {
                Fail(
                    "anomaly_model_missing",
                    $"异常模型库 {pin.LibraryId} r{pin.LibraryRevision} 无法读取：{error.Message}"
                );
                return findings;
            }

            if (library.Id != pin.LibraryId || library.Revision != pin.LibraryRevision)
            {
                throw new InvalidOperationException(
                    "Anomaly model provider substituted the pinned revision."
                );
            }

            if (pin.PerCharacter)
            {
                return ValidateCharacterModels(r, library, findings, Fail);
            }

            string key = pin.KeyFor(r.Name);
            if (library.Models.TryGetValue(key, out var scoped) && scoped.Scope != EAnomalyModelScope.Region)
            {
                Fail(
                    "anomaly_model_scope_mismatch",
                    $"模型[{key}]是字符模型；整ROI模式需要ROI模型，逐字符检查请在绑定中选择逐字符模式。"
                );
                return findings;
            }

            if (!library.Models.TryGetValue(key, out var entry))
            {
                // 按字符组列出：“组：字符…”，未分组的只列字符。
                var characters = library
                    .Models.Values.Where(m => m.Scope == EAnomalyModelScope.Character)
                    .GroupBy(m => m.Group)
                    .Select(g =>
                        (g.Key == null ? "" : g.Key + "：")
                        + string.Concat(g.Select(m => m.Character).OrderBy(c => c, StringComparer.Ordinal))
                    )
                    .ToArray();
                var regions = library
                    .Models.Values.Where(m => m.Scope == EAnomalyModelScope.Region)
                    .Select(m => m.Key)
                    .ToArray();
                string hint =
                    r.Kind == ERegionKind.Text && characters.Length > 0
                        ? $"库中是字符模型（{string.Join("；", characters)}）：文字行请在ROI编辑中把“逐字符检查”设为是，模型键填字符组。"
                    : regions.Length > 0
                        ? "库中现有整ROI模型："
                            + string.Join("、", regions)
                            + "；请为本ROI训练，或在ROI编辑中填写对应的模型键。"
                    : "库中还没有整ROI模型；请在“批量训练(B)”中为本ROI训练并发布。";
                Fail(
                    "anomaly_model_missing",
                    $"异常模型库 {library.Name} r{library.Revision} 中没有模型[{key}]。" + hint
                );
                return findings;
            }

            if (!_owner._anomalyImplementations.Contains(entry.FeatureSource))
            {
                Fail(
                    "anomaly_feature_unavailable",
                    $"模型[{key}]使用特征来源{entry.FeatureSource}，宿主未提供对应实现（如CNN骨干网络）。"
                );
                return findings;
            }

            var lease = _owner._anomalyCache.Acquire(entry, _owner._anomalyImplementations);
            _anomalyLeases.Add(lease);
            var runtime = lease.Runtime;

            if (
                entry.LocalRadius > 0
                && (long)r.Bounds.X + r.Bounds.Width <= _request.ImageWidth
                && (long)r.Bounds.Y + r.Bounds.Height <= _request.ImageHeight
            )
            {
                var crop = new RegionAnomalyDetector() { Margin = entry.Margin }.CropFor(
                    _request.ImageWidth,
                    _request.ImageHeight,
                    r.Bounds
                );
                if (crop.Width != entry.Width || crop.Height != entry.Height)
                {
                    Fail(
                        "anomaly_model_size_mismatch",
                        $"位置相关模型[{key}]按{entry.Width}×{entry.Height}裁图训练，当前ROI裁图为{crop.Width}×{crop.Height}；ROI已改变，需重新训练。"
                    );
                    return findings;
                }
            }

            _anomaly[r.Name] = (entry, runtime);
            return findings;
        }

        /// <summary>逐字符模式需要OCR身份来分割并选择字符模型；显式等格用格位标签，不需要读取。</summary>
        /// <param name = "r">当前ROI配置。</param>
        public bool AnomalyNeedsReading(InspectionRegion r)
        {
            return r.Anomaly?.PerCharacter == true && r.Kind == ERegionKind.Text && !r.Field.EqualCells;
        }

        private IReadOnlyList<InspectionFinding> ValidateCharacterModels(
            InspectionRegion r,
            AnomalyLibrarySnapshot library,
            List<InspectionFinding> findings,
            Action<string, string> fail
        )
        {
            string? group = r.Anomaly!.ModelKey;
            var all = library.Models.Values.Where(m => m.Scope == EAnomalyModelScope.Character).ToArray();
            var entries = all.Where(m => m.Group == group).ToArray();
            if (entries.Length == 0)
            {
                var groups = all.Select(m => m.Group ?? "（未分组）").Distinct().ToArray();
                fail(
                    "anomaly_model_missing",
                    $"异常模型库 {library.Name} r{library.Revision} 中没有"
                        + (group == null ? "未分组的字符模型" : $"字符组[{group}]的字符模型")
                        + (
                            groups.Length == 0
                                ? "，库中还没有字符模型；请在“批量训练(B)”中训练并发布。"
                                : "；库中现有字符组："
                                    + string.Join("、", groups)
                                    + "，请在ROI编辑的“模型键”中填写对应的字符组。"
                        )
                );
                return findings;
            }

            var unavailable = entries
                .Select(e => e.FeatureSource)
                .Distinct()
                .Where(f => !_owner._anomalyImplementations.Contains(f))
                .ToArray();
            if (unavailable.Length > 0)
            {
                fail(
                    "anomaly_feature_unavailable",
                    "字符模型使用特征来源"
                        + string.Join("、", unavailable)
                        + "，宿主未提供对应实现（如CNN骨干网络）。"
                );
                return findings;
            }

            var models = new Dictionary<string, CharacterAnomalyModel>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                var lease = _owner._anomalyCache.Acquire(e, _owner._anomalyImplementations);
                _anomalyLeases.Add(lease);
                if (r.Anomaly!.InkLoss && !(lease.Runtime is DP.Vision.OpenCv.PatchAnomalyRuntime))
                {
                    fail("anomaly_capability_unavailable", "所选原生模型不支持旧手工ink_loss能力，请在配方明确关闭缺墨附加检查。");
                    return findings;
                }
                models[e.Character!] = new CharacterAnomalyModel(e, lease.Runtime);
            }

            if (r.Field.Expected != null)
            {
                if (!A.CharacterIdentity.TryTokenizeLine(r.Field.Expected, out var required))
                {
                    fail(
                        "anomaly_character_identity_invalid",
                        "B逐字符检查的预期行包含无效单字身份（空白行、控制字符或组合序列）。"
                    );
                    return findings;
                }
                var missing = required
                    .Distinct(StringComparer.Ordinal)
                    .Where(c => !models.ContainsKey(c))
                    .ToArray();
                if (missing.Length > 0)
                {
                    fail(
                        "anomaly_character_model_missing",
                        "已知必需字符缺少字符异常模型：" + string.Join("", missing)
                    );
                }
            }

            _characterModels[r.Name] = models;
            return findings;
        }

        /// <summary>逐字符模式：复用方法A的分割（若有），否则按OCR读数或等格声明分割，逐字与字符模型比较。</summary>
        private RoiQualityMeasurement InspectCharacters(
            InspectionRegion r,
            RegionInspectionResult evidence,
            CancellationToken token
        )
        {
            if (!_characterModels.TryGetValue(r.Name, out var models))
            {
                throw new InvalidOperationException(
                    "Character anomaly models were not validated for this ROI."
                );
            }

            var pin = r.Anomaly!;
            var segmentation = evidence.Segmentation;
            if (
                segmentation == null
                || segmentation.Status != "provisional" && segmentation.Status != "explicit_cells"
            )
            {
                using var source = _request.VisionSource!.Retain();
                using var measured = r.Field.EqualCells
                    ? _owner._segmenter.EqualCells(source, r.Bounds, r.Field.Expected!)
                    : _owner._segmenter.Segment(source, r.Bounds, evidence.Recognition?.Text ?? "", token);
                segmentation = Bridge.ToLabel(measured);
            }

            if (segmentation.Status != "provisional" && segmentation.Status != "explicit_cells")
            {
                return new RoiQualityMeasurement(
                    new RegionInspectionResult(
                        r.Name,
                        new[]
                        {
                            new InspectionFinding(
                                "anomaly_segmentation_failed",
                                "B逐字符检查需要可靠的字符分割：" + segmentation.Reason,
                                EInspectionVerdict.Ng,
                                r.Bounds
                            ).WithExecutionBlocker(true),
                        }
                    ),
                    false
                );
            }

            Func<string, CharacterAnomalyModel?> lookup = c => models.TryGetValue(c, out var m) ? m : null;
            var detector = new CharacterAnomalyDetector();
            using var inspectionSource = _request.VisionSource!.Retain();
            var result = detector.Inspect(
                inspectionSource,
                segmentation.Characters,
                r.Bounds,
                lookup,
                token,
                pin.InkLoss
            );
            var findings = result.Scores.SelectMany(s => s.Findings).ToList();
            var compared = result.Scores.Where(s => s.Status == "compared").ToArray();
            var worst = compared.OrderByDescending(s => s.Ratio).FirstOrDefault();
            var failed = result.Scores.Where(s => !s.Passed).ToArray();
            findings.Add(
                new InspectionFinding(
                    "anomaly_summary",
                    $"B逐字符异常检测（{pin.LibraryId} r{pin.LibraryRevision}"
                        + (
                            pin.ModelKey == null
                                ? "，未分组：字符组功能之前训练的模型，各行字体合在一起，同一字符的阈值偏高、易漏检，"
                                    + "请在“批量训练(B)”中重新训练发布并一键绑定"
                                : $"，字符组[{pin.ModelKey}]"
                        )
                        + $"）：{result.Scores.Count}字中{compared.Length}字已检测"
                        + (
                            worst == null
                                ? ""
                                : $"，最大为字符[{worst.Character}]（第{worst.TokenIndex + 1}位）{worst.Ratio:F2}倍阈值"
                        )
                        + (
                            failed.Length == 0
                                ? "，全部通过。"
                                : "；未通过："
                                    + string.Join(
                                        " ",
                                        failed.Select(s =>
                                            $"[{s.Character}]第{s.TokenIndex + 1}位"
                                            + (
                                                s.Status == "compared"
                                                    ? $"{s.Ratio:F2}倍"
                                                    : "（" + s.Status + "）"
                                            )
                                        )
                                    )
                                    + "。"
                        )
                        + "逐字（倍数）："
                        + string.Join(
                            " ",
                            result.Scores.Select(s =>
                                s.Character + (s.Status == "compared" ? s.Ratio.ToString("F2") : "-")
                            )
                        ),
                    EInspectionVerdict.Ok,
                    r.Bounds
                )
            );
            var used = compared
                .Select(s => models[s.Character].Entry)
                .Distinct()
                .OrderBy(e => e.Key)
                .ToArray();
            string combined;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                combined = BitConverter
                    .ToString(
                        sha.ComputeHash(
                            System.Text.Encoding.UTF8.GetBytes(
                                string.Join(",", used.Select(e => e.Key + ":" + e.Sha256))
                            )
                        )
                    )
                    .Replace("-", "")
                    .ToLowerInvariant();
            }

            return new RoiQualityMeasurement(
                new RegionInspectionResult(r.Name, findings).WithAnomaly(
                    new RegionAnomalyEvidence(
                        pin.LibraryId,
                        pin.LibraryRevision,
                        "per-character",
                        combined,
                        string.Join(",", used.Select(e => e.FeatureSource).Distinct()),
                        result.Crop,
                        result.WorstRatio,
                        1,
                        result.HeatMap
                    )
                ),
                result.Completed
            );
        }

        /// <summary>在已定位ROI上执行方法B，返回异常区域、得分摘要及热力图证据。</summary>
        /// <param name = "r">已定位的ROI。</param>
        /// <param name = "evidence">本轮已有读取/分割证据，逐字符模式使用。</param>
        /// <param name = "token">协作式取消标记。</param>
        public RoiQualityMeasurement InspectAnomaly(
            InspectionRegion r,
            RegionInspectionResult evidence,
            CancellationToken token
        )
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (r.Anomaly?.PerCharacter == true)
            {
                return InspectCharacters(r, evidence, token);
            }

            if (!_anomaly.TryGetValue(r.Name, out var bound) || r.Anomaly == null)
            {
                throw new InvalidOperationException("Anomaly model was not validated for this ROI.");
            }

            var (entry, runtime) = bound;
            using var source = _request.VisionSource!.Retain();
            var result = new RegionAnomalyDetector() { Margin = entry.Margin }.Inspect(
                source, r, runtime, new A.AnomalyDetectionOptions(entry.Threshold, entry.MinimumArea), token);
            var findings = result.Findings.ToList();
            int anomalies = findings.Count(f => f.Verdict == EInspectionVerdict.Ng && f.Bounds.HasValue);
            findings.Add(
                new InspectionFinding(
                    "anomaly_summary",
                    $"B异常检测：模型[{entry.Key}]（{r.Anomaly.LibraryId} r{r.Anomaly.LibraryRevision}，"
                        + entry.FeatureSource
                        + (entry.LocalRadius > 0 ? $"，位置相关±{entry.LocalRadius}px" : "，与位置无关")
                        + $"，{entry.TrainingImages}张良品）；最大得分{result.MaximumScore:F3}，阈值{result.Threshold:F3}（{result.Ratio:F2}倍），异常区域{anomalies}处。",
                    EInspectionVerdict.Ok,
                    r.Bounds
                )
            );
            return new RoiQualityMeasurement(
                new RegionInspectionResult(r.Name, findings).WithAnomaly(
                    new RegionAnomalyEvidence(
                        r.Anomaly.LibraryId,
                        r.Anomaly.LibraryRevision,
                        entry.Key,
                        entry.Sha256,
                        entry.FeatureSource,
                        result.Crop,
                        result.MaximumScore,
                        result.Threshold,
                        result.HeatMap
                    )
                ),
                result.Completed
            );
        }
    }
}
