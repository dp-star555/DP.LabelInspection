using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace DP.LabelInspection.Tests;

/// <summary>在两个CLR目标上运行真实原生OpenCV，并执行与后台无关的策略测试。</summary>
[TestClass]
public sealed partial class InspectionTests
{
    private static byte[] Pixels()
    {
        return Enumerable.Repeat((byte)255, 120 * 80).ToArray();
    }

    private static void Fill(byte[] data, PixelRect box, byte value)
    {
        for (int y = box.Y; y < box.Y + box.Height; y++)
        {
            for (int x = box.X; x < box.X + box.Width; x++)
            {
                data[y * 120 + x] = value;
            }
        }
    }

    private static ImageFrame Frame(byte[] data)
    {
        return new ImageFrame(120, 80, EImagePixelFormat.Gray8, data);
    }

    private static InspectionRegion Fixed()
    {
        return new InspectionRegion("key", ERegionKind.Fixed, new PixelRect(5, 5, 70, 60));
    }

    private static InspectionOptions NoQualityGate(int tolerance = 0)
    {
        return new InspectionOptions(tolerancePixels: tolerance, minimumContrast: 0, minimumSharpness: 0);
    }

    private static InspectionRequest Request(
        byte[] actual,
        byte[]? reference,
        InspectionRegion[] regions,
        InspectionOptions? options = null,
        EAlignmentMode alignment = EAlignmentMode.AssumeAligned
    )
    {
        return new InspectionRequest(
            Frame(actual),
            new InspectionRecipe(
                "test",
                120,
                80,
                reference == null ? EInspectionMode.Free : EInspectionMode.Template,
                alignment,
                regions,
                options ?? NoQualityGate()
            ),
            reference == null ? null : Frame(reference)
        );
    }

    private static InspectionReport Run(InspectionRequest request)
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        return engine.Inspect(request);
    }

    /// <summary>托管缓冲区不与宿主可变数组共享存储。</summary>
    [TestMethod]
    public void FrameOwnsItsPixels()
    {
        var input = Pixels();
        var image = Frame(input);
        input[0] = 0;
        var output = image.CopyPixels();
        output[1] = 0;
        Assert.AreEqual((byte)255, image.CopyPixels()[0]);
        Assert.AreEqual((byte)255, image.CopyPixels()[1]);
    }

    /// <summary>拒绝错误的内存布局。</summary>
    [TestMethod]
    public void InvalidFrameLayoutRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ImageFrame(10, 10, EImagePixelFormat.Bgr24, new byte[100])
        );
    }

    /// <summary>实测缺墨产生NG，不依赖强制全图关卡。</summary>
    [TestMethod]
    public void MissingInkDetected()
    {
        var reference = Pixels();
        Fill(reference, new PixelRect(20, 20, 30, 30), 0);
        var actual = (byte[])reference.Clone();
        Fill(actual, new PixelRect(20, 20, 10, 30), 255);
        var result = Run(
            Request(actual, reference, new[] { Fixed() }, new InspectionOptions(tolerancePixels: 0))
        );
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        var finding = result.Analysis.Regions[0].Findings.Single();
        Assert.AreEqual("missing_ink", finding.Code);
        Assert.AreEqual(300, finding.AreaPixels);
        Assert.AreEqual(20, finding.Bounds!.Value.X);
    }

    /// <summary>多墨与良好参考产生差异。</summary>
    [TestMethod]
    public void ExtraInkDetected()
    {
        var actual = Pixels();
        Fill(actual, new PixelRect(20, 20, 12, 12), 0);
        var result = Run(Request(actual, Pixels(), new[] { Fixed() }));
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        Assert.AreEqual("extra_ink", result.Analysis.Regions[0].Findings.Single().Code);
    }

    /// <summary>未改变的明确固定范围可以通过。</summary>
    [TestMethod]
    public void IdenticalFixedRegionPassesOnlyItsScope()
    {
        var image = Pixels();
        Fill(image, new PixelRect(20, 20, 25, 25), 0);
        Assert.AreEqual(EInspectionVerdict.Ok, Run(Request(image, image, new[] { Fixed() })).Verdict);
    }

    /// <summary>可检测声明为空白的ROI内墨迹。</summary>
    [TestMethod]
    public void BlankSpotDetected()
    {
        var image = Pixels();
        Fill(image, new PixelRect(91, 21, 6, 8), 0);
        var result = Run(
            Request(
                image,
                null,
                new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(80, 10, 30, 50)) }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        Assert.AreEqual(48, result.Analysis.Regions[0].Findings.Single().AreaPixels);
    }

    /// <summary>自由模式空白ROI干净，不代表整张标签通过。</summary>
    [TestMethod]
    public void FreeModePassesOnlyConfiguredCleanScope()
    {
        Assert.AreEqual(
            EInspectionVerdict.Ok,
            Run(
                Request(
                    Pixels(),
                    null,
                    new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(5, 5, 50, 50)) }
                )
            ).Verdict
        );
    }

    /// <summary>未配置的标签范围仍属于未覆盖。</summary>
    [TestMethod]
    public void NoRegionsFailsClosed()
    {
        Assert.AreEqual(
            EInspectionVerdict.Ng,
            Run(Request(Pixels(), Pixels(), Array.Empty<InspectionRegion>())).Verdict
        );
    }

    /// <summary>忽略区域内变化不产生缺陷。</summary>
    [TestMethod]
    public void IgnoreRegionMasksDifferences()
    {
        var image = Pixels();
        Fill(image, new PixelRect(20, 20, 12, 12), 0);
        var result = Run(
            Request(
                image,
                Pixels(),
                new[]
                {
                    Fixed(),
                    new InspectionRegion("ignore", ERegionKind.Ignore, new PixelRect(18, 18, 16, 16)),
                }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ok, result.Verdict);
    }

    /// <summary>完全被掩码排除的检查不能空通过。</summary>
    [TestMethod]
    public void EntirelyIgnoredCheckFailsClosed()
    {
        var region = Fixed();
        Assert.AreEqual(
            EInspectionVerdict.Ng,
            Run(
                Request(
                    Pixels(),
                    Pixels(),
                    new[] { region, new InspectionRegion("ignore", ERegionKind.Ignore, region.Bounds) }
                )
            ).Verdict
        );
    }

    /// <summary>形态学扩展区不能让完全排除的有效范围被判成功。</summary>
    [TestMethod]
    public void IgnoreHaloCannotVacuouslyPass()
    {
        var result = Run(
            Request(
                Pixels(),
                Pixels(),
                new[]
                {
                    Fixed(),
                    new InspectionRegion("ignore", ERegionKind.Ignore, new PixelRect(6, 5, 69, 60)),
                },
                NoQualityGate(1)
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        Assert.IsTrue(result.Analysis.Regions[0].Findings.Any(f => f.Code == "empty_effective_scope"));
    }

    /// <summary>未要求的全图采集指标不再降低已选印刷质量缺陷的严重程度。</summary>
    [TestMethod]
    public void UnrequestedGlobalQualityCannotDowngradeMeasuredNg()
    {
        var reference = Pixels();
        Fill(reference, new PixelRect(20, 20, 20, 20), 0);
        var result = Run(Request(Pixels(), reference, new[] { Fixed() }, new InspectionOptions()));
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        Assert.IsTrue(
            result.Analysis.Regions.SelectMany(r => r.Findings).Any(f => f.Verdict == EInspectionVerdict.Ng)
        );
        Assert.IsTrue(
            double.IsNaN(result.Analysis.Contrast),
            "Unrequested global image-quality gate must not run."
        );
    }

    /// <summary>平移能力不可用时不能静默使用未配准参考坐标。</summary>
    [TestMethod]
    public void MissingRegistrationSkipsDefinitiveDifference()
    {
        var reference = Pixels();
        Fill(reference, new PixelRect(20, 20, 20, 20), 0);
        var result = Run(
            Request(Pixels(), reference, new[] { Fixed() }, alignment: EAlignmentMode.Translation)
        );
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        StringAssert.Contains(result.Analysis.Regions[0].Findings.Single().Message, "alignment_failed");
    }

    /// <summary>计划功能明确标记不可用，不返回伪成功。</summary>
    [TestMethod]
    [DataRow(ERegionKind.Text)]
    [DataRow(ERegionKind.Barcode)]
    public void MissingRequiredCapabilitiesFailRoi(ERegionKind kind)
    {
        var report = Run(
            Request(
                Pixels(),
                Pixels(),
                new[] { new InspectionRegion("pending", kind, new PixelRect(5, 5, 40, 40)) }
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsTrue(
            report
                .Analysis.Regions[0]
                .Findings.Any(f =>
                    f.Code == (kind == ERegionKind.Text ? "ocr_unavailable" : "barcode_unavailable")
                )
        );
    }

    /// <summary>固定字段不能与可变值使用相同像素。</summary>
    [TestMethod]
    public void OverlappingNonignoredRegionsRejected()
    {
        Assert.AreEqual(
            EInspectionVerdict.Ng,
            Run(
                Request(
                    Pixels(),
                    Pixels(),
                    new[]
                    {
                        Fixed(),
                        new InspectionRegion("value", ERegionKind.Text, new PixelRect(10, 10, 30, 30)),
                    }
                )
            ).Verdict
        );
    }

    /// <summary>仍拒绝不安全重叠，并指出两个冲突ROI。</summary>
    [TestMethod]
    [DataRow(ERegionKind.Text, ERegionKind.Text)]
    [DataRow(ERegionKind.Barcode, ERegionKind.Barcode)]
    [DataRow(ERegionKind.Fixed, ERegionKind.Text)]
    [DataRow(ERegionKind.Fixed, ERegionKind.Barcode)]
    [DataRow(ERegionKind.Blank, ERegionKind.Text)]
    [DataRow(ERegionKind.Blank, ERegionKind.Barcode)]
    public void ConflictingRegionsHaveActionableMessage(ERegionKind first, ERegionKind second)
    {
        var report = Run(
            Request(
                Pixels(),
                Pixels(),
                new[]
                {
                    new InspectionRegion("ROI-A", first, new PixelRect(5, 5, 40, 40)),
                    new InspectionRegion("ROI-B", second, new PixelRect(10, 10, 30, 30)),
                }
            )
        );
        Assert.AreEqual(2, report.Analysis.Regions.Count);
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.IsTrue(report.Analysis.Regions.All(r => r.Findings.Any(f => f.Code == "roi_overlap")));
        var messages = string.Join(
            ";",
            report.Analysis.Regions.SelectMany(r => r.Findings).Select(f => f.Message)
        );
        Assert.IsTrue(messages.Contains("ROI-A") && messages.Contains("ROI-B"));
    }

    /// <summary>原图坐标必须保持在边界内。</summary>
    [TestMethod]
    public void OutOfBoundsRejected()
    {
        Assert.IsTrue(
            Run(
                Request(
                    Pixels(),
                    null,
                    new[] { new InspectionRegion("out", ERegionKind.Blank, new PixelRect(110, 10, 30, 30)) }
                )
            )
                .Analysis.Regions.Single()
                .Findings.Any(f => f.Code == "roi_outside_image")
        );
    }

    /// <summary>固定检查必须有参考。</summary>
    [TestMethod]
    public void FixedInFreeModeRejected()
    {
        Assert.AreEqual(EInspectionVerdict.Ng, Run(Request(Pixels(), null, new[] { Fixed() })).Verdict);
    }

    /// <summary>取消向外传播，不产生放行结果。</summary>
    [TestMethod]
    public async Task CancellationPropagates()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            engine.InspectAsync(Request(Pixels(), null, Array.Empty<InspectionRegion>()), cancel.Token)
        );
    }

    /// <summary>Core与契约不引用UI或原生实现程序集。</summary>
    [TestMethod]
    public void CoreHasNoNativeOrUiAssemblyDependencies()
    {
        foreach (var assembly in new[] { typeof(ImageFrame).Assembly, typeof(InspectionEngine).Assembly })
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert.IsFalse(
                    new[]
                    {
                        "OpenCvSharp",
                        "halcondotnet",
                        "System.Drawing",
                        "System.Windows.Forms",
                        "PresentationFramework",
                    }.Contains(reference.Name)
                );
            }
        }
    }

    /// <summary>第二种非原生适配器证明Core可通过公开接口替换后台。</summary>
    [TestMethod]
    public void MissingBackendCoverageCannotPass()
    {
        var backend = new IncompleteBackend();
        using var engine = new InspectionEngine(backend, ownsBackend: true);
        Assert.AreEqual(
            EInspectionVerdict.Review,
            engine.Inspect(Request(Pixels(), Pixels(), new[] { Fixed() })).Verdict
        );
        engine.Dispose();
        engine.Dispose();
        Assert.AreEqual(1, backend.DisposeCalls);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            engine.Inspect(Request(Pixels(), null, Array.Empty<InspectionRegion>()))
        );
    }
}
