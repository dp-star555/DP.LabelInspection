using System;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.Vision;
using V = DP.Vision;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>以统一图像源调用现有检测引擎，封装输入保留及检测请求的租约生命周期，不替换分阶段检测策略。</summary>
public static class InspectionSourceExtensions
{
    /// <summary>
    /// 返回任务前保留实际图和可选参考图，调用方随后可释放自己的源。
    /// 检测请求直接借用这两个租约，不再复制为标签快照；帧身份由本入口生成，像素布局与尺寸校验仍由请求入口执行。
    /// </summary>
    /// <param name="engine">宿主注入的检测引擎，任务完成前不得释放引擎。</param>
    /// <param name="actual">借用的实际图像源。</param>
    /// <param name="recipe">固定配方快照。</param>
    /// <param name="reference">可选的借用参考源，缺少必要参考仍由正式流程阻断。</param>
    /// <param name="cycleId">可选采集周期标识，不从图像内容推断。</param>
    /// <param name="taskData">本周期的外部业务数据。</param>
    /// <param name="cancellationToken">协作取消；失败或取消也会释放内部源租约。</param>
    /// <returns>完整检测报告，其证据为独立业务快照，不借用已释放的输入源。</returns>
    public static Task<InspectionReport> InspectAsync(
        this IInspectionEngine engine,
        IImageSource actual,
        InspectionRecipe recipe,
        IImageSource? reference = null,
        string? cycleId = null,
        TaskDataSnapshot? taskData = null,
        CancellationToken cancellationToken = default
    )
    {
        if (engine == null)
            throw new ArgumentNullException(nameof(engine));
        if (actual == null)
            throw new ArgumentNullException(nameof(actual));
        if (recipe == null)
            throw new ArgumentNullException(nameof(recipe));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<InspectionReport>(cancellationToken);

        // 必须在返回任务之前保留参考图；实际图由ImageProcessing同步保留。
        var referenceLease = reference?.Retain();
        return RunAsync();

        async Task<InspectionReport> RunAsync()
        {
            using (referenceLease)
            {
                return await ImageProcessing
                    .RunAsync(
                        actual,
                        async (lease, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            // 帧与请求都在本作用域内建立；成功、取消和异常都由using归还租约。
                            using var actualFrame = new V.ImageFrame(NewFrameId(), lease);
                            using var referenceFrame =
                                referenceLease == null ? null : new V.ImageFrame(NewFrameId(), referenceLease);
                            using var request = InspectionRequest.FromVision(
                                actualFrame,
                                recipe,
                                referenceFrame,
                                cycleId,
                                taskData
                            );
                            return await engine.InspectAsync(request, token).ConfigureAwait(false);
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>生成本次调用的帧身份；与旧请求在未提供外部身份时的行为一致。</summary>
    private static string NewFrameId() => Guid.NewGuid().ToString("N");
}
