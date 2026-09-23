using System;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.Vision;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>以统一图像源调用现有检测引擎，封装输入保留及不可变业务快照转换，不替换分阶段检测策略。</summary>
public static class InspectionSourceExtensions
{
    /// <summary>
    /// 返回任务前保留实际图和可选参考图，调用方随后可释放自己的源。
    /// 内部仍复制为标签快照，支持Gray8/Bgr24并遵守标签尺寸限制；不是零复制入口。
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
                            var actualSnapshot = AlgorithmContractAdapter.ToLabel(lease);
                            token.ThrowIfCancellationRequested();
                            var referenceSnapshot =
                                referenceLease == null
                                    ? null
                                    : AlgorithmContractAdapter.ToLabel(referenceLease);
                            token.ThrowIfCancellationRequested();
                            var request = new InspectionRequest(
                                actualSnapshot,
                                recipe,
                                referenceSnapshot,
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
}
