using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>整图模板与独立尺寸字符参考保持区分。</summary>
[TestClass]
public sealed class ReferenceModeTests
{
    /// <summary>尺寸不同的过期整图模板被拒绝并给出具体尺寸，不静默缩放或丢弃。</summary>
    [TestMethod]
    public void MismatchedWholeReferenceExplainsRecovery()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var image = new PixelSnapshot(32, 16, EImagePixelFormat.Gray8, new byte[512]);
        var reference = new PixelSnapshot(8, 8, EImagePixelFormat.Gray8, new byte[64]);
        var recipe = new InspectionRecipe(
            "test",
            32,
            16,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[] { new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(0, 0, 32, 16)) }
        );
        var report = engine.Inspect(TestRequests.FromSnapshot(image, recipe, reference));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        var message = report.Analysis.Regions.Single().Findings.Single().Message;
        StringAssert.Contains(message, "32×16");
        StringAssert.Contains(message, "8×8");
        StringAssert.Contains(message, "单字库");
    }

    /// <summary>固定和空白质量直接裁取Vision租约；整图参考不需要转成标签快照。</summary>
    [TestMethod]
    [DataRow(ERegionKind.Fixed)]
    [DataRow(ERegionKind.Blank)]
    public void VisionFramesRunSurfaceQuality(ERegionKind kind)
    {
        var bytes = Enumerable.Repeat((byte)255, 32 * 16).ToArray();
        if (kind == ERegionKind.Fixed)
            for (int y = 4; y < 12; y++)
            for (int x = 8; x < 24; x++)
                bytes[y * 32 + x] = 0;
        using var image = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(32, 16, DP.Vision.EPixelLayout.Gray8),
            bytes
        );
        using var actual = new DP.Vision.ImageFrame("actual-surface", image);
        using var reference = new DP.Vision.ImageFrame("reference-surface", image);
        var recipe = new InspectionRecipe(
            "surface",
            32,
            16,
            kind == ERegionKind.Fixed ? EInspectionMode.Template : EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[] { new InspectionRegion("surface", kind, new PixelRect(0, 0, 32, 16)) }
        );
        using var request = InspectionRequest.FromVision(
            actual,
            recipe,
            kind == ERegionKind.Fixed ? reference : null
        );
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        Assert.AreEqual(
            EInspectionVerdict.Ok,
            report.Verdict,
            string.Join(";", report.Analysis.Regions.Single().Findings.Select(f => f.Code))
        );
        Assert.AreEqual(ERoiStageState.Passed, report.Analysis.Regions.Single().Execution!.Quality);
        Assert.AreEqual("actual-surface", report.Analysis.Alignment!.FrameId);
    }

    /// <summary>真实ECC平移定位直接读取Vision租约，不依赖旧标签图像快照。</summary>
    [TestMethod]
    public void VisionFramesRunTranslationRegistration()
    {
        var bytes = new byte[48 * 48];
        new Random(42).NextBytes(bytes);
        using var source = DP.Vision.VisionImage.CopyFrom(
            new DP.Vision.ImageInfo(48, 48, DP.Vision.EPixelLayout.Gray8),
            bytes
        );
        using var actual = new DP.Vision.ImageFrame("ecc-actual", source);
        using var reference = new DP.Vision.ImageFrame("ecc-reference", source);
        var recipe = new InspectionRecipe(
            "position",
            48,
            48,
            EInspectionMode.Template,
            EAlignmentMode.Translation,
            new[]
            {
                new InspectionRegion("anchor", ERegionKind.Fixed, new PixelRect(0, 0, 48, 48)).WithTasks(
                    new RoiInspectionTasks(false, true)
                ),
            }
        );
        using var request = InspectionRequest.FromVision(actual, recipe, reference);
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        Assert.IsTrue(
            report.Analysis.Alignment!.Measured,
            string.Join(";", report.Analysis.Regions.Single().Findings.Select(f => f.Code))
        );
        Assert.AreEqual(0, report.Analysis.OffsetX);
        Assert.AreEqual(0, report.Analysis.OffsetY);
    }

    /// <summary>明确模板模式但无模板时仍报错，不隐式切换为自由模式。</summary>
    [TestMethod]
    public void MissingWholeReferenceStillRejects()
    {
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var image = new PixelSnapshot(32, 16, EImagePixelFormat.Gray8, new byte[512]);
        var recipe = new InspectionRecipe(
            "test",
            32,
            16,
            EInspectionMode.Template,
            EAlignmentMode.AssumeAligned,
            new[] { new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(0, 0, 32, 16)) }
        );
        var report = engine.Inspect(TestRequests.FromSnapshot(image, recipe));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        StringAssert.Contains(report.Analysis.Regions.Single().Findings.Single().Message, "未载入");
    }

    /// <summary>自建小字符库可用于真实外观检测，无需整图参考。</summary>
    [TestMethod]
    public void UserCreatedGlyphNeedsNoWholeImageReference()
    {
        string root = Path.Combine(Path.GetTempPath(), "reference-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pixels = Enumerable.Repeat((byte)255, 64 * 32).ToArray();
            for (int y = 3; y < 17; y++)
            {
                pixels[(y + 4) * 64 + 12] = 0;
                pixels[(y + 4) * 64 + 19] = 0;
            }

            for (int x = 4; x < 12; x++)
            {
                pixels[14 * 64 + 8 + x] = 0;
            }

            var image = new PixelSnapshot(64, 32, EImagePixelFormat.Gray8, pixels);
            var store = new InspectionStore(root, new OpenCvImageCodec());
            string id = store.CreateLibrary("user-font");
            var session = new GlyphDraftSession();
            session.LoadImage(image);
            var box = new PixelRect(8, 4, 16, 20);
            string candidate = session.AddManual(box, "H");
            session.Stage(id, new[] { candidate }, Array.Empty<string>());
            int revision = session.Publish(store, id, 1);
            var region = new InspectionRegion(
                "glyph",
                ERegionKind.Text,
                box,
                field: new FieldSettings(id, revision, "H", equalCells: true)
            );
            using var backend = new OpenCvInspectionBackend(libraries: store);
            using var engine = new InspectionEngine(backend);
            var report = engine.Inspect(
                TestRequests.FromSnapshot(
                    image,
                    new InspectionRecipe(
                        "glyph",
                        64,
                        32,
                        EInspectionMode.Free,
                        EAlignmentMode.AssumeAligned,
                        new[] { region },
                        new InspectionOptions(minimumContrast: 0, minimumSharpness: 0)
                    )
                )
            );
            Assert.AreEqual(16, store.Load(id, revision).Glyphs["H"].Image.Width);
            Assert.AreEqual("compared", report.Analysis.Regions[0].Glyphs.Single().Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
