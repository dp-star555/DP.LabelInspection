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

    private int _maximumParallelRois = 1;

    /// <summary>
    /// 一次检测内最多并行执行的ROI数（默认1，即按配方顺序串行）。大于1时，不从其他ROI取引导值的ROI并行执行，
    /// 前提是后台会话实现 <see cref = "IConcurrentRoiSession"/>；报告仍按配方顺序输出，判定与串行相同。
    /// </summary>
    public int MaximumParallelRois
    {
        get => _maximumParallelRois;
        set => _maximumParallelRois = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

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
            if (request.ImageWidth != request.Recipe.Width || request.ImageHeight != request.Recipe.Height)
            {
                throw new ArgumentException("Recipe and image dimensions differ.");
            }

            return RoiWorkflow.Run(request, _backend, _maximumParallelRois, cancellationToken);
        }
    }

    /// <summary>异步提取字库制作候选，与正式验收分割分离；同一引擎上的原生工作仍串行执行。</summary>
    /// <param name = "frame">借用Vision原图，调用时立即Retain；宿主可在返回任务后释放自己的句柄。</param>
    /// <param name = "bounds">候选区域的原图像素范围，必须完全位于frame内。</param>
    /// <param name = "confirmedText">人工确认的可选文本，用于候选标注，不改写正式检测读数。</param>
    /// <param name = "token">协作式取消标记，在后台算法调用前后检查。</param>
    /// <returns>包含候选、标注和待复核原因的异步结果，不代表已批准发布字库。</returns>
    public async Task<GlyphCandidateExtraction> ExtractGlyphCandidatesAsync(
        DP.Vision.IImageSource frame,
        PixelRect bounds,
        string? confirmedText = null,
        CancellationToken token = default
    )
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame.Info.Width, frame.Info.Height))
        {
            throw new ArgumentException("Candidate ROI outside image.");
        }

        using var lease = frame.Retain();
        return await Task.Run(
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
                    var result = source.ExtractGlyphCandidates(lease, bounds, confirmedText, token);
                    token.ThrowIfCancellationRequested();
                    return result ?? throw new InvalidOperationException("No extraction result.");
                }
            },
            token
        ).ConfigureAwait(false);
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
