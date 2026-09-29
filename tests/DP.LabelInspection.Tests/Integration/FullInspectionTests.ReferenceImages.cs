using System.Linq;
using System.Runtime.InteropServices;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.LabelInspection.Tests;

public sealed partial class FullInspectionTests
{
    /// <summary>
    /// 文字质检的参考图按固定版本只转换一次：两个互不重叠的 ROI 连续两次检测共享同一份已转换租约，
    /// 转换次数不随 ROI 数或检测次数增长；检测结束后只剩缓存自己那份租约，后台释放后归零。
    /// </summary>
    [TestMethod]
    public void TextQualityReusesConvertedReferenceImagesAcrossRoisAndCycles()
    {
        using var temp = new TempStore();
        string id = temp.Store.CreateLibrary("regular");
        int revision = temp.Store.PutGlyph(id, 1, "A", Glyph(), "fixed");
        using var raw = new Mat(40, 180, MatType.CV_8UC1, Scalar.All(255));
        using var glyph = new Mat();
        using (var source = new Mat(40, 30, MatType.CV_8UC1))
        {
            var bytes = Glyph().CopyPixels();
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            source.CopyTo(glyph);
        }

        for (int cell = 0; cell < 6; cell++)
        {
            using var target = new Mat(raw, new Rect(cell * 30, 0, 30, 40));
            glyph.CopyTo(target);
        }

        var frame = Frame(raw);
        var cells = new InspectionRegion(
            "cells",
            ERegionKind.Text,
            new PixelRect(0, 0, 90, 40),
            field: new FieldSettings(id, revision, "AAA", equalCells: true)
        );
        var repeat = new InspectionRegion(
            "again",
            ERegionKind.Text,
            new PixelRect(90, 0, 90, 40),
            field: new FieldSettings(id, revision, "AAA", equalCells: true)
        );
        var converter = new CountingReferenceConverter();
        var libraries = new CountingGlyphLibraries(temp.Store);
        using var backend = new OpenCvInspectionBackend(
            libraries: libraries,
            referenceImageConverter: converter
        );
        using var engine = new InspectionEngine(backend);
        var recipe = new InspectionRecipe(
            "cells",
            180,
            40,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[] { cells, repeat },
            new InspectionOptions(minimumContrast: 0, minimumSharpness: 0)
        );

        var once = engine.Inspect(TestRequests.FromSnapshot(frame, recipe));
        var twice = engine.Inspect(TestRequests.FromSnapshot(frame, recipe));

        Assert.AreEqual(1, libraries.Loads, "固定版本字库只从存储载入一次");
        Assert.AreEqual(2, once.Analysis.Regions.Count);
        foreach (var region in once.Analysis.Regions)
        {
            Assert.AreEqual(3, region.Glyphs.Count);
            Assert.AreEqual(3, region.Glyphs.Count(g => g.Comparison != null), "参考图确实参与了比较");
        }

        Assert.AreEqual(
            1,
            converter.Conversions,
            "一个字库参考图只转换一次：不随 ROI 数、也不随检测次数增长"
        );
        CollectionAssert.AreEqual(
            once.Analysis.Regions.SelectMany(r => r.Glyphs.Select(g => g.Status)).ToArray(),
            twice.Analysis.Regions.SelectMany(r => r.Glyphs.Select(g => g.Status)).ToArray()
        );
        Assert.AreEqual(1, converter.LiveLeases, "检测结束后只剩缓存自己那一份参考图租约");

        backend.Dispose();
        Assert.AreEqual(0, converter.LiveLeases, "后台释放后参考图租约归零");
    }
}
