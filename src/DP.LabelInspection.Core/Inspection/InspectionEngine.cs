using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>与具体后台无关的请求校验、原生工作串行化及判定策略。</summary>
/// <remarks>Dispose会等待当前调用结束；UI关闭时应先取消并等待后台任务，再释放自己拥有的引擎。</remarks>
public sealed class InspectionEngine : IInspectionEngine, IGlyphCandidateService, IDisposable
{
    private readonly object _sync = new object();
    private readonly IInspectionBackend _backend;
    private readonly bool _ownsBackend;
    private bool _disposed;

    /// <summary>通过宿主明确选择的后台创建引擎，不使用全局服务定位器。</summary>
    /// <param name = "backend">宿主提供的具体后台适配器。</param>
    /// <param name = "ownsBackend">是否把后台释放责任交给引擎；false时由宿主释放后台。</param>
    public InspectionEngine(IInspectionBackend backend, bool ownsBackend = false)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _ownsBackend = ownsBackend;
    }

    /// <inheritdoc/>
    public EInspectionCapabilities Capabilities => _backend.Capabilities;

    /// <inheritdoc/>
    public Task<InspectionReport> InspectAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        return Task.Run(() => Inspect(request, cancellationToken), cancellationToken);
    }

    /// <summary>同步执行无界面检测，同一引擎上的调用串行处理。</summary>
    /// <param name = "request">不可变检测请求，包含原图、配方、引导值及参考资源。</param>
    /// <param name = "cancellationToken">取消标记，在原生工作前后检查；不承诺立即中断厂商内部调用。</param>
    /// <returns>包含覆盖情况、阶段状态、原始证据和最终判定的报告。</returns>
    public InspectionReport Inspect(InspectionRequest request, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InspectionEngine));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_backend is IRoiWorkflowBackend staged)
            {
                if (
                    request.Actual.Width != request.Recipe.Width
                    || request.Actual.Height != request.Recipe.Height
                )
                {
                    throw new ArgumentException("Recipe and image dimensions differ.");
                }

                return RoiWorkflow.Run(request, _backend, staged, cancellationToken);
            }

            Validate(request);
            var evaluatedAt = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            var analysis =
                _backend.Analyze(request, cancellationToken)
                ?? throw new InvalidOperationException("Backend returned no analysis.");
            cancellationToken.ThrowIfCancellationRequested();
            var global = new List<InspectionFinding>();
            var expected = request
                .Recipe.Regions.Where(r => r.Kind != ERegionKind.Ignore)
                .Select(r => r.Name)
                .ToArray();
            bool discovery =
                request.Recipe.Mode == EInspectionMode.Free
                && request.Recipe.Regions.Count == 0
                && Capabilities.HasFlag(EInspectionCapabilities.Discovery);
            if (
                !discovery
                && (
                    analysis.Regions.Count != expected.Length
                    || !analysis
                        .Regions.Select(r => r.RegionName)
                        .OrderBy(n => n)
                        .SequenceEqual(expected.OrderBy(n => n))
                )
            )
            {
                global.Add(
                    new InspectionFinding(
                        "incomplete_backend_result",
                        "Backend did not account for every requested region.",
                        EInspectionVerdict.Review
                    )
                );
            }

            foreach (var region in request.Recipe.Regions.Where(r => r.Kind != ERegionKind.Ignore))
            {
                var required =
                    region.Kind == ERegionKind.Fixed ? EInspectionCapabilities.FixedDifference
                    : region.Kind == ERegionKind.Blank ? EInspectionCapabilities.BlankSpots
                    : region.Kind == ERegionKind.Text
                        ? (
                            region.Field.EqualCells
                                ? EInspectionCapabilities.None
                                : EInspectionCapabilities.Ocr
                        )
                            | (
                                (region.Field.LibraryId != null || region.Field.EqualCells)
                                    ? EInspectionCapabilities.CharacterSegmentation
                                        | EInspectionCapabilities.GlyphComparison
                                    : EInspectionCapabilities.None
                            )
                    : EInspectionCapabilities.BarcodeDecode
                        | (
                            region.Field.BarcodePrint.Enabled
                                ? EInspectionCapabilities.BarcodeStructure
                                : EInspectionCapabilities.None
                        );
                if ((Capabilities & required) != required)
                {
                    global.Add(
                        new InspectionFinding(
                            "capability_unavailable",
                            region.Name + ": adapter does not implement all requested checks.",
                            EInspectionVerdict.Review
                        )
                    );
                }
            }

            if (
                request.Recipe.Mode == EInspectionMode.Template
                && request.Recipe.Alignment == EAlignmentMode.Translation
                && !Capabilities.HasFlag(EInspectionCapabilities.TranslationAlignment)
            )
            {
                global.Add(
                    new InspectionFinding(
                        "alignment_unavailable",
                        "Requested translation registration is not implemented by this adapter.",
                        EInspectionVerdict.Review
                    )
                );
            }

            var options = request.Recipe.Options;
            bool finite =
                !double.IsNaN(analysis.Contrast)
                && !double.IsInfinity(analysis.Contrast)
                && !double.IsNaN(analysis.Sharpness)
                && !double.IsInfinity(analysis.Sharpness);
            bool qualityPassed =
                Capabilities.HasFlag(EInspectionCapabilities.Quality)
                && finite
                && analysis.Contrast >= options.MinimumContrast
                && analysis.Sharpness >= options.MinimumSharpness;
            analysis = FieldBindingEvaluator.Apply(
                request,
                analysis,
                qualityPassed,
                evaluatedAt,
                Capabilities
            );
            if (!qualityPassed)
            {
                global.Add(
                    new InspectionFinding(
                        "image_quality_review",
                        "Quality assessment is absent or below configured thresholds; defect candidates require review.",
                        EInspectionVerdict.Review
                    )
                );
                analysis = new BackendAnalysis(
                    analysis.Contrast,
                    analysis.Sharpness,
                    analysis.Regions.Select(r => new RegionInspectionResult(
                        r.RegionName,
                        r.Findings.Select(f => new InspectionFinding(
                            f.Code,
                            f.Message,
                            f.Verdict == EInspectionVerdict.Ng
                            && !(
                                f.Code == "barcode_not_decoded"
                                && Capabilities.HasFlag(EInspectionCapabilities.BarcodeDecode)
                            )
                                ? EInspectionVerdict.Review
                                : f.Verdict,
                            f.Bounds,
                            f.AreaPixels
                        )),
                        r.Recognition,
                        r.Segmentation,
                        r.Glyphs,
                        r.Barcodes
                    )),
                    analysis.OffsetX,
                    analysis.OffsetY
                );
            }

            // 即使外观证据因图像质量而降级，执行未完成仍须保持NG，不能当作通过。
            analysis = RequiredAppearancePolicy.Apply(request, analysis);
            if (request.Recipe.Mode == EInspectionMode.Free || expected.Length == 0)
            {
                global.Add(
                    new InspectionFinding(
                        "uncovered_scope",
                        "No full-label release guarantee; only declared regions were considered.",
                        EInspectionVerdict.Review
                    )
                );
            }

            var all = global.Concat(analysis.Regions.SelectMany(r => r.Findings)).ToArray();
            var verdict =
                all.Any(f => f.Verdict == EInspectionVerdict.Ng) ? EInspectionVerdict.Ng
                : all.Any(f => f.Verdict == EInspectionVerdict.Review) ? EInspectionVerdict.Review
                : EInspectionVerdict.Ok;
            return new InspectionReport(
                _backend.Name,
                verdict,
                analysis,
                global,
                watch.Elapsed.TotalMilliseconds
            );
        }
    }

    /// <summary>异步提取字库制作候选，与正式验收分割分离；同一引擎上的原生工作仍串行执行。</summary>
    /// <param name = "frame">不可变原始图像，供本次候选提取读取。</param>
    /// <param name = "bounds">候选区域的原图像素范围，必须完全位于frame内。</param>
    /// <param name = "confirmedText">人工确认的可选文本，用于候选标注，不改写正式检测读数。</param>
    /// <param name = "token">协作式取消标记，在后台算法调用前后检查。</param>
    /// <returns>包含候选、标注和待复核原因的异步结果，不代表已批准发布字库。</returns>
    public Task<GlyphCandidateExtraction> ExtractGlyphCandidatesAsync(
        ImageFrame frame,
        PixelRect bounds,
        string? confirmedText = null,
        CancellationToken token = default
    )
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Candidate ROI outside image.");
        }

        return Task.Run(
            () =>
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(InspectionEngine));
                    }

                    token.ThrowIfCancellationRequested();
                    var source =
                        _backend as IGlyphCandidateBackend
                        ?? throw new NotSupportedException(
                            "Backend has no glyph candidate extraction capability."
                        );
                    var result = source.ExtractGlyphCandidates(frame, bounds, confirmedText, token);
                    token.ThrowIfCancellationRequested();
                    return result ?? throw new InvalidOperationException("No extraction result.");
                }
            },
            token
        );
    }

    private static void Validate(InspectionRequest request)
    {
        var recipe = request.Recipe;
        if (request.Actual.Width != recipe.Width || request.Actual.Height != recipe.Height)
        {
            throw new ArgumentException("Recipe and image dimensions differ.");
        }

        if (
            recipe.Mode == EInspectionMode.Template
            && (
                request.Reference == null
                || request.Reference.Width != recipe.Width
                || request.Reference.Height != recipe.Height
            )
        )
        {
            throw new ArgumentException(
                "当前为整图模板模式，参考整图必须与待检图同尺寸。待检/配方："
                    + recipe.Width
                    + "×"
                    + recipe.Height
                    + "；参考："
                    + (
                        request.Reference == null
                            ? "未载入"
                            : request.Reference.Width + "×" + request.Reference.Height
                    )
                    + "。单字库图块不需要与整图同尺寸；若仅做单字库检查，请切换“无整图参考（可用单字库）”（SDK使用Free模式且Reference=null），ROI中的字库绑定保留。若需要整图模板检查，请载入匹配尺寸的参考整图，不要拉伸单字图块。"
            );
        }

        foreach (var region in recipe.Regions)
        {
            if (!region.Bounds.Fits(request.Actual))
            {
                throw new ArgumentException("ROI lies outside the image: " + region.Name);
            }

            if (recipe.Mode == EInspectionMode.Free && region.Kind == ERegionKind.Fixed)
            {
                throw new ArgumentException("Fixed-region checks require template mode.");
            }
        }

        var checks = recipe.Regions.Where(r => r.Kind != ERegionKind.Ignore).ToArray();
        for (int i = 0; i < checks.Length; i++)
        {
            for (int j = i + 1; j < checks.Length; j++)
            {
                var a = checks[i];
                var b = checks[j];
                bool textAndBarcode =
                    (a.Kind == ERegionKind.Text && b.Kind == ERegionKind.Barcode)
                    || (a.Kind == ERegionKind.Barcode && b.Kind == ERegionKind.Text);
                // 条码ROI可能同时包含人眼可读文字；码解码与OCR各自保留独立结果。
                if (a.Bounds.Intersects(b.Bounds) && !textAndBarcode)
                {
                    throw new ArgumentException(
                        $"ROI区域冲突：{a.Name}（{a.Kind}，{a.Bounds}）与 {b.Name}（{b.Kind}，{b.Bounds}）重叠。仅允许文字与条码交叠；固定内容、空白检查及同类型ROI请分开。忽略区可覆盖检查区域。"
                    );
                }
            }
        }
    }

    /// <summary>等待当前工作结束后释放引擎拥有的后台；可安全重复调用。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsBackend)
            {
                _backend.Dispose();
            }
        }
    }
}
