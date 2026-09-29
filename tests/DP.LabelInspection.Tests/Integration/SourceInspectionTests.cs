using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.Vision;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>通过客户统一源入口验证真实引擎接入和双输入的异步租约，不修改正式判定策略。</summary>
[TestClass]
public sealed class SourceInspectionTests
{
    /// <summary>创建不依赖全图质量门槛的固定区域配方。</summary>
    private static InspectionRecipe Recipe()
    {
        return new InspectionRecipe(
            "source",
            32,
            16,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[] { new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(0, 0, 16, 16)) },
            new InspectionOptions(tolerancePixels: 0, minimumContrast: 0, minimumSharpness: 0)
        );
    }

    /// <summary>两个互不重叠的固定区，用于确认多ROI不会逐ROI重复整帧复制。</summary>
    private static InspectionRecipe TwoRegionRecipe()
    {
        return new InspectionRecipe(
            "source-two",
            32,
            16,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[]
            {
                new InspectionRegion("left", ERegionKind.Fixed, new PixelRect(0, 0, 16, 16)),
                new InspectionRegion("right", ERegionKind.Fixed, new PixelRect(16, 0, 16, 16)),
            },
            new InspectionOptions(tolerancePixels: 0, minimumContrast: 0, minimumSharpness: 0)
        );
    }

    private static byte[] Pixels(byte value) => Enumerable.Repeat(value, 512).ToArray();

    /// <summary>真实分阶段引擎可以直接接收源；客户立即释放输入不会破坏检测，且全程不物化标签整帧。</summary>
    [TestMethod]
    public async Task RealEngineAcceptsUnifiedSource()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var probe = new LeaseProbe(VisionImage.CopyFrom(new ImageInfo(32, 16, EPixelLayout.Gray8), Pixels(255)));
        var work = engine.InspectAsync(probe, Recipe(), probe);
        probe.Dispose();
        var report = await work;
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual(1, report.Analysis.Regions.Count);
        Assert.AreEqual(0, probe.WholeFrameCopies, "统一源入口不得再复制标签整帧。");
        Assert.IsTrue(probe.RowCopies > 0, "运行期必须真的通过租约读到像素，否则本用例只是空转。");
        Assert.AreEqual(0, probe.LiveLeases, "任务结束后输入租约必须全部归还。");
    }

    /// <summary>多ROI时整帧复制仍为0；每个ROI各自借用租约，不重复复制原图。</summary>
    [TestMethod]
    public async Task MultipleRoisDoNotMaterializeLabelWholeFrame()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var probe = new LeaseProbe(VisionImage.CopyFrom(new ImageInfo(32, 16, EPixelLayout.Gray8), Pixels(255)));
        var work = engine.InspectAsync(probe, TwoRegionRecipe(), probe);
        probe.Dispose();
        var report = await work;

        Assert.AreEqual(2, report.Analysis.Regions.Count, "两个ROI都必须真的跑完。");
        Assert.AreEqual(0, probe.WholeFrameCopies, "多ROI不得逐ROI重复整帧复制。");
        Assert.IsTrue(probe.RowCopies > 0, "运行期必须真的通过租约读到像素。");
        Assert.AreEqual(0, probe.LiveLeases, "任务结束后输入租约必须全部归还。");
    }

    /// <summary>报告证据是独立业务快照，不借用输入源；输入释放后报告仍完整可读。</summary>
    [TestMethod]
    public async Task ReportEvidenceDoesNotHoldInputLease()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var probe = new LeaseProbe(VisionImage.CopyFrom(new ImageInfo(32, 16, EPixelLayout.Gray8), Pixels(255)));
        var report = await engine.InspectAsync(probe, Recipe(), probe);

        probe.Dispose();
        Assert.AreEqual(0, probe.LiveLeases, "报告不得持有输入租约；证据必须是独立快照。");
        Assert.AreEqual(EInspectionVerdict.Ok, report.Verdict);
        Assert.AreEqual(1, report.Analysis.Regions.Count, "输入源释放后报告仍须完整。");
        Assert.AreEqual("fixed", report.Analysis.Regions[0].RegionName);
    }

    /// <summary>实际和参考源在排队前都已保留，完成、异常和取消均归还全部池化槽位。</summary>
    /// <param name="outcome">0成功，1算法异常，2协作取消。</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [Timeout(10000)]
    public async Task BothInputsRemainOwnedUntilEngineCompletes(int outcome)
    {
        using var pool = new FrameBufferPool(new ImageInfo(32, 16, EPixelLayout.Gray8), 2, 1024);
        pool.TryRent(out var first);
        pool.TryRent(out var second);
        using var firstWriter = first!;
        using var secondWriter = second!;
        firstWriter.Write(0, Pixels(11), 0, 512);
        secondWriter.Write(0, Pixels(22), 0, 512);
        var actual = firstWriter.Publish();
        var reference = secondWriter.Publish();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new SourceInspectionEngine(
            async (request, token) =>
            {
                entered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                // 输入像素只能经Vision租约读取；检测请求不再暴露标签快照。
                Assert.IsNotNull(request.VisionSource, "检测请求必须持有Vision原图租约。");
                Assert.IsNotNull(request.VisionReference, "检测请求必须持有Vision参考租约。");
                Assert.AreEqual((byte)11, FirstByte(request.VisionSource));
                Assert.AreEqual((byte)22, FirstByte(request.VisionReference));
                Assert.AreEqual("cycle-source", request.CycleId);
                token.ThrowIfCancellationRequested();
                if (outcome == 1)
                    throw new InvalidOperationException("检测异常测试");
                // 此报告仅是租约测试的显式替身，不冒充实际检测成功。
                return new InspectionReport(
                    "lease-test",
                    EInspectionVerdict.Ng,
                    new BackendAnalysis(double.NaN, double.NaN, Array.Empty<RegionInspectionResult>()),
                    Array.Empty<InspectionFinding>(),
                    0
                );
            }
        );
        var task = engine.InspectAsync(
            actual,
            Recipe(),
            reference,
            "cycle-source",
            cancellationToken: cancellation.Token
        );
        try
        {
            actual.Dispose();
            reference.Dispose();
            await entered.Task;
            if (outcome == 2)
                cancellation.Cancel();
            Assert.IsFalse(pool.TryRent(out _));
        }
        finally
        {
            actual.Dispose();
            reference.Dispose();
            gate.TrySetResult(true);
        }
        if (outcome == 1)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await task);
        else if (outcome == 2)
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        else
            Assert.AreEqual("lease-test", (await task).Backend);
        Assert.IsTrue(pool.TryRent(out var returnedFirst));
        Assert.IsTrue(pool.TryRent(out var returnedSecond));
        returnedFirst!.Dispose();
        returnedSecond!.Dispose();
    }

    /// <summary>实际源保留失败时，已经保留的参考源也必须释放。</summary>
    [TestMethod]
    public async Task FailedActualRetainReleasesReference()
    {
        using var pool = new FrameBufferPool(new ImageInfo(32, 16, EPixelLayout.Gray8), 1, 512);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var reference = writer.Publish();
        var actual = VisionImage.CopyFrom(reference.Info, new byte[512]);
        actual.Dispose();
        var engine = new SourceInspectionEngine((_, __) => throw new InvalidOperationException("不应执行"));
        var task = engine.InspectAsync(actual, Recipe(), reference);
        reference.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await task);
        Assert.IsTrue(pool.TryRent(out var returned));
        returned!.Dispose();
    }

    /// <summary>不支持的标签位深必须显式失败，不能静默降为8位，且失败不能漏掉租约。</summary>
    [TestMethod]
    public async Task UnsupportedLayoutFailsAndReleasesSource()
    {
        using var pool = new FrameBufferPool(new ImageInfo(32, 16, EPixelLayout.Gray16), 1, 1024);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var source = writer.Publish();
        var engine = new SourceInspectionEngine((_, __) => throw new InvalidOperationException("不应执行"));
        var task = engine.InspectAsync(source, Recipe());
        source.Dispose();
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await task);
        Assert.IsTrue(pool.TryRent(out var returned));
        returned!.Dispose();
    }

    /// <summary>从Vision租约读取首字节，替代原先经标签快照取像素的做法。</summary>
    private static byte FirstByte(IImageSource source)
    {
        var buffer = new byte[1];
        source.CopyTo(0, buffer, 0, 1);
        return buffer[0];
    }
}
