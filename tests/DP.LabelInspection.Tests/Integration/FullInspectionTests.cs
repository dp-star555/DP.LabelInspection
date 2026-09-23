using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

/// <summary>真实原生分割、比较、配准，托管条码及持久化流程回归。</summary>
[TestClass]
public sealed partial class FullInspectionTests
{
    /// <summary>重叠投影保留自身笔画及微小噪点，移除已知邻字墨迹。</summary>
    [TestMethod]
    public void ComponentOwnershipDoesNotRecropNeighbors()
    {
        using var image = new Mat(30, 32, MatType.CV_8UC1, Scalar.All(255));
        Cv2.Line(image, new Point(5, 5), new Point(15, 20), Scalar.All(0));
        Cv2.Line(image, new Point(13, 5), new Point(23, 20), Scalar.All(0));
        image.Set(23, 6, (byte)0);
        var result = new CharacterSegmenter().Segment(Frame(image), new PixelRect(0, 0, 32, 30), "AB");
        Assert.AreEqual("provisional", result.Status);
        Assert.AreEqual("connected_component_ownership", result.Basis);
        Assert.AreEqual(2, result.Characters.Count);
        Assert.IsTrue(result.Characters.Sum(c => c.NeighborInkRemoved) > 0);
        var first = result.Characters[0];
        var pixels = first.Patch.CopyPixels();
        Assert.AreEqual((byte)0, pixels[(23 - first.Bounds.Y) * first.Patch.Stride + 6 - first.Bounds.X]);
        Assert.AreEqual((byte)255, pixels[(5 - first.Bounds.Y) * first.Patch.Stride + 13 - first.Bounds.X]);
        Assert.AreEqual((byte)0, pixels[(5 - first.Bounds.Y) * first.Patch.Stride + 5 - first.Bounds.X]);
    }

    /// <summary>不能仅为匹配OCR数量分割真正连接的笔画。</summary>
    [TestMethod]
    public void TouchingAndClippingRequireReview()
    {
        using var image = new Mat(30, 32, MatType.CV_8UC1, Scalar.All(255));
        Cv2.Rectangle(image, new Rect(4, 5, 20, 18), Scalar.All(0), -1);
        Assert.AreEqual(
            "uncertain",
            new CharacterSegmenter().Segment(Frame(image), new PixelRect(0, 0, 32, 30), "AB").Status
        );
        Cv2.Rectangle(image, new Rect(0, 0, 32, 30), Scalar.All(0), -1);
        Assert.AreNotEqual(
            "provisional",
            new CharacterSegmenter().Segment(Frame(image), new PixelRect(0, 0, 32, 30), "A").Status
        );
    }

    /// <summary>不支持的身份和有色标注不被强行转换为字形。</summary>
    [TestMethod]
    public void UnsupportedAndColoredLinesRejected()
    {
        using var image = new Mat(30, 40, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(image, new Rect(5, 5, 15, 20), new Scalar(0, 0, 0), -1);
        Cv2.Rectangle(image, new Rect(25, 5, 5, 10), new Scalar(0, 255, 0), -1);
        var frame = Frame(image);
        Assert.AreEqual(
            "unsupported",
            new CharacterSegmenter().Segment(frame, new PixelRect(0, 0, 40, 30), "字").Status
        );
        Assert.AreEqual(
            "uncertain",
            new CharacterSegmenter().Segment(frame, new PixelRect(0, 0, 40, 30), "AB").Status
        );
    }

    /// <summary>点状部件保留到垂直对齐的主笔画。</summary>
    [TestMethod]
    public void DotAndStemAreOnePhysicalGroup()
    {
        using var image = new Mat(30, 24, MatType.CV_8UC1, Scalar.All(255));
        Cv2.Rectangle(image, new Rect(8, 5, 3, 3), Scalar.All(0), -1);
        Cv2.Rectangle(image, new Rect(8, 11, 3, 15), Scalar.All(0), -1);
        var result = new CharacterSegmenter().Segment(Frame(image), new PixelRect(0, 0, 24, 30), "i");
        Assert.AreEqual(1, result.Characters.Count);
        Assert.AreEqual("provisional", result.Status);
    }

    /// <summary>验证归一化参考相同、擦除墨迹及空参考语义。</summary>
    [TestMethod]
    public void GlyphDifferencesAreMeasuredInNormalizedSpace()
    {
        var reference = Glyph();
        var comparer = new GlyphComparer();
        var entry = new GlyphReference("A", reference, "test", "fixed");
        Assert.AreEqual(0d, comparer.Compare(reference, entry).Difference);
        using var blank = new Mat(40, 30, MatType.CV_8UC1, Scalar.All(255));
        var missing = comparer.Compare(Frame(blank), entry);
        Assert.AreEqual(1d, missing.Difference);
        Assert.AreEqual(112, missing.Delta.Width);
        Assert.AreEqual(
            "empty_reference",
            comparer.Compare(reference, new GlyphReference("A", Frame(blank), "empty", "fixed")).Status
        );
    }

    /// <summary>明确重复的等格字符复用同一参考，不受字典顺序影响。</summary>
    [TestMethod]
    public void EqualCellsReusePinnedCharacterAndDetectMissingInk()
    {
        using var temp = new TempStore();
        string id = temp.Store.CreateLibrary("regular");
        int revision = temp.Store.PutGlyph(id, 1, "A", Glyph(), "fixed");
        using var raw = new Mat(40, 90, MatType.CV_8UC1, Scalar.All(255));
        using var glyph = new Mat();
        using (var source = new Mat(40, 30, MatType.CV_8UC1))
        {
            var bytes = Glyph().CopyPixels();
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            source.CopyTo(glyph);
        }

        using (var left = new Mat(raw, new Rect(0, 0, 30, 40)))
        {
            glyph.CopyTo(left);
        }

        using (var right = new Mat(raw, new Rect(60, 0, 30, 40)))
        {
            glyph.CopyTo(right);
        }

        var frame = Frame(raw);
        var region = new InspectionRegion(
            "cells",
            ERegionKind.Text,
            new PixelRect(0, 0, 90, 40),
            field: new FieldSettings(id, revision, "AAA", equalCells: true)
        );
        using var backend = new OpenCvInspectionBackend(libraries: temp.Store);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(
            new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "cells",
                    90,
                    40,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { region },
                    new InspectionOptions(minimumContrast: 0, minimumSharpness: 0)
                )
            )
        );
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        var chars = report.Analysis.Regions[0].Glyphs;
        Assert.AreEqual(3, chars.Count);
        Assert.AreEqual(1, chars.Select(c => c.ReferenceSha256).Distinct().Count());
        Assert.AreEqual("exceeds_threshold", chars[1].Status);
        Assert.AreEqual("compared", chars[2].Status);
    }

    /// <summary>真实ECC仅从固定内容估计有界平移，并比较实际原图坐标像素。</summary>
    [TestMethod]
    public void BoundedRegistrationMapsActualCoordinates()
    {
        using var reference = new Mat(100, 180, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(reference, "DP123", new Point(22, 52), HersheyFonts.HersheySimplex, 1, Scalar.All(0), 2);
        using var matrix = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        matrix.Set(0, 0, 1f);
        matrix.Set(1, 1, 1f);
        matrix.Set(0, 2, 3f);
        matrix.Set(1, 2, 2f);
        using var actual = new Mat();
        Cv2.WarpAffine(
            reference,
            actual,
            matrix,
            new Size(180, 100),
            InterpolationFlags.Nearest,
            BorderTypes.Constant,
            Scalar.All(255)
        );
        var recipe = new InspectionRecipe(
            "align",
            180,
            100,
            EInspectionMode.Template,
            EAlignmentMode.Translation,
            new[] { new InspectionRegion("fixed", ERegionKind.Fixed, new PixelRect(10, 10, 150, 75)) }
        );
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var result = engine.Inspect(new InspectionRequest(Frame(actual), recipe, Frame(reference)));
        Assert.AreEqual(3, result.Analysis.OffsetX);
        Assert.AreEqual(2, result.Analysis.OffsetY);
        Assert.AreEqual(EInspectionVerdict.Ok, result.Verdict);
    }

    /// <summary>真实一维码及QR解码、预期不符、缺少解码器与印刷评级保持区分。</summary>
    [TestMethod]
    [DataRow(BarcodeFormat.CODE_128)]
    [DataRow(BarcodeFormat.QR_CODE)]
    public void RealBarcodeDecodeAndContentRules(BarcodeFormat format)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = 380,
                Height = format == BarcodeFormat.QR_CODE ? 380 : 140,
                Margin = 12,
            },
        };
        var encoded = writer.Write("DP12345");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var symbols = new ZxingBarcodeDecoder().Decode(frame, bounds, default);
        Assert.AreEqual("DP12345", symbols.Single().Text);
        using var backend = new OpenCvInspectionBackend(barcode: new ZxingBarcodeDecoder());
        using var engine = new InspectionEngine(backend);
        var recipe = new InspectionRecipe(
            "barcode",
            frame.Width,
            frame.Height,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[]
            {
                new InspectionRegion(
                    "code",
                    ERegionKind.Barcode,
                    bounds,
                    field: new FieldSettings(expected: "WRONG")
                ),
            }
        );
        var report = engine.Inspect(new InspectionRequest(frame, recipe));
        Assert.AreEqual(EInspectionVerdict.Ng, report.Verdict);
        Assert.AreEqual(1, report.Analysis.Regions[0].Barcodes.Count);
        Assert.IsTrue(report.Analysis.Regions[0].Findings.Any(f => f.Code == "content_mismatch"));
    }

    /// <summary>新导入不覆盖或重新绑定；编辑、删除、归档后旧版本仍可读取。</summary>
    [TestMethod]
    public void LibraryHistoryProvenanceAndStaleEdits()
    {
        using var temp = new TempStore();
        var store = temp.Store;
        string id = store.CreateLibrary("regular");
        int rev = store.PutGlyph(id, 1, "A", Glyph(), provenanceJson: "{\"field\":\"source\"}");
        string before = store.ExportLibrary(id, rev);
        string imported = store.ImportLibrary(before);
        Assert.AreNotEqual(id, imported);
        Assert.AreEqual(
            "source",
            (string?)store.ReadLibrary(imported, 1)["glyphs"]!["A"]!["provenance"]!["field"]
        );
        Assert.ThrowsExactly<InvalidOperationException>(() => store.PutGlyph(id, 1, "B", Glyph()));
        Assert.AreEqual(1, store.Load(id, rev).Glyphs.Count);
        int deleted = store.RemoveGlyph(id, rev, "A");
        Assert.AreEqual(0, store.Load(id, deleted).Glyphs.Count);
        store.Archive(id, deleted);
        Assert.IsFalse(store.ListLibraries().Any(l => l.Id == id));
        Assert.AreEqual(1, store.Load(id, rev).Glyphs.Count);
        var doc = JObject.Parse(before);
        doc["glyphs"]!["A"]!["sha256"] = "bad";
        Assert.ThrowsExactly<InvalidDataException>(() => store.ImportLibrary(doc.ToString()));
        Assert.ThrowsExactly<ArgumentException>(() => store.Load("../outside", 1));
    }

    /// <summary>原生配方往返保留等格及规则参数，不需要原生类型。</summary>
    [TestMethod]
    public void RecipeRoundtripPreservesBindingsAndGeometry()
    {
        using var temp = new TempStore();
        var recipe = new InspectionRecipe(
            "roundtrip",
            100,
            50,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            new[]
            {
                new InspectionRegion(
                    "text",
                    ERegionKind.Text,
                    new PixelRect(3, 4, 40, 30),
                    true,
                    new FieldSettings("regular", 7, "ABC", "[A-Z]+", maximumDifference: .22)
                ),
            }
        );
        var restored = temp.Store.DeserializeRecipe(temp.Store.SerializeRecipe(recipe));
        Assert.AreEqual(3, restored.Regions[0].Bounds.X);
        Assert.AreEqual(7, restored.Regions[0].Field.LibraryRevision);
        Assert.AreEqual("ABC", restored.Regions[0].Field.Expected);
        Assert.AreEqual(.22, restored.Regions[0].Field.MaximumDifference);
    }

    /// <summary>报告带精确快照原子发布，人工反馈不覆盖机器判定。</summary>
    [TestMethod]
    public void ReportZipContainsTraceableInputsAndFeedback()
    {
        using var temp = new TempStore();
        var frame = Glyph();
        using var backend = new OpenCvInspectionBackend();
        using var engine = new InspectionEngine(backend);
        var request = new InspectionRequest(
            frame,
            new InspectionRecipe(
                "blank",
                30,
                40,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[] { new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(0, 0, 30, 40)) }
            )
        );
        var report = engine.Inspect(request);
        string id = temp.Store.SaveReport(request, report, frame);
        temp.Store.AddFeedback(id, "REVIEW", "needs operator", "test");
        string zip = Path.Combine(temp.Path, "report.zip");
        temp.Store.ExportReport(id, zip);
        using var archive = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read);
        Assert.IsNotNull(archive.GetEntry("actual.png"));
        Assert.IsNotNull(archive.GetEntry("recipe.json"));
        Assert.IsNotNull(archive.GetEntry("manifest.json"));
        Assert.IsTrue(archive.Entries.Any(e => e.FullName.StartsWith("feedback/")));
        Assert.AreEqual(report.Verdict.ToString(), (string?)temp.Store.History().Single()["verdict"]);
    }

    /// <summary>并发编辑不能覆盖已发布版本。</summary>
    [TestMethod]
    public void ConcurrentWritersPublishOnlyOneRevision()
    {
        using var temp = new TempStore();
        string id = temp.Store.CreateLibrary("case");
        int success = 0;
        System.Threading.Tasks.Parallel.For(
            0,
            2,
            i =>
            {
                try
                {
                    temp.Store.PutGlyph(id, 1, i == 0 ? "A" : "a", Glyph());
                    System.Threading.Interlocked.Increment(ref success);
                }
                catch (InvalidOperationException) { }
            }
        );
        Assert.AreEqual(1, success);
        Assert.AreEqual(2, temp.Store.Latest(id));
        Assert.AreEqual(1, temp.Store.Load(id, 2).Glyphs.Count);
        int next = temp.Store.PutGlyph(id, 2, "B", Glyph());
        Assert.AreEqual(2, temp.Store.Load(id, next).Glyphs.Count);
    }

    /// <summary>大小写敏感标签和报告快照在后续编辑后仍固定原版本。</summary>
    [TestMethod]
    public void ReportReferencesDoNotFollowLaterLibraryEdits()
    {
        using var temp = new TempStore();
        var store = temp.Store;
        string id = store.CreateLibrary("regular");
        store.PutGlyph(id, 1, "A", Glyph());
        store.PutGlyph(id, 2, "a", Glyph());
        Assert.AreEqual(2, store.Load(id, 3).Glyphs.Count);
        var frame = Glyph();
        var request = new InspectionRequest(
            frame,
            new InspectionRecipe(
                "pinned",
                30,
                40,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[]
                {
                    new InspectionRegion(
                        "cell",
                        ERegionKind.Text,
                        new PixelRect(0, 0, 30, 40),
                        field: new FieldSettings(id, 2, "A", equalCells: true)
                    ),
                }
            )
        );
        using var backend = new OpenCvInspectionBackend(libraries: store);
        using var engine = new InspectionEngine(backend);
        var report = engine.Inspect(request);
        string job = store.SaveReport(request, report);
        var snapshot = JObject.Parse(
            File.ReadAllText(Path.Combine(temp.Path, "jobs", job, "libraries.json"))
        );
        Assert.AreEqual(2, (int)snapshot[id + "@2"]!["revision"]!);
        Assert.IsNull(snapshot[id + "@2"]!["glyphs"]!["a"]);
        File.AppendAllText(Path.Combine(temp.Path, "jobs", job, "report.json"), " ");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.ExportReport(job, Path.Combine(temp.Path, "corrupted.zip"))
        );
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "corrupted.zip")));
    }

    /// <summary>固定或忽略ROI默认值可序列化，不生成无效字段设置。</summary>
    [TestMethod]
    public void FixedRecipeRoundtripAndFractionalRectRejection()
    {
        using var temp = new TempStore();
        var recipe = new InspectionRecipe(
            "fixed",
            80,
            40,
            EInspectionMode.Template,
            EAlignmentMode.Translation,
            new[] { new InspectionRegion("key", ERegionKind.Fixed, new PixelRect(4, 5, 50, 30)) }
        );
        string json = temp.Store.SerializeRecipe(recipe);
        Assert.AreEqual(ERegionKind.Fixed, temp.Store.DeserializeRecipe(json).Regions.Single().Kind);
        var doc = JObject.Parse(json);
        doc["regions"]![0]!["bounds"]![0] = 4.5;
        Assert.ThrowsExactly<InvalidDataException>(() => temp.Store.DeserializeRecipe(doc.ToString()));
    }

    /// <summary>即使其他行仍能解码一维码内容，也报告受损扫描行。</summary>
    [TestMethod]
    public void BarcodePrintCandidateIsNotIsoGrade()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = 400,
                Height = 150,
                Margin = 15,
            },
        };
        var encoded = writer.Write("DP12345");
        var pixels = new byte[encoded.Width * encoded.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = encoded.Pixels[i * 4];
        }

        for (int y = 75; y < 80; y++)
        {
            for (int x = 0; x < encoded.Width; x++)
            {
                pixels[y * encoded.Width + x] = 255;
            }
        }

        var frame = new ImageFrame(encoded.Width, encoded.Height, EImagePixelFormat.Gray8, pixels);
        using var backend = new OpenCvInspectionBackend(barcode: new ZxingBarcodeDecoder());
        using var engine = new InspectionEngine(backend);
        var request = new InspectionRequest(
            frame,
            new InspectionRecipe(
                "stripe",
                frame.Width,
                frame.Height,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[]
                {
                    new InspectionRegion(
                        "barcode",
                        ERegionKind.Barcode,
                        new PixelRect(0, 0, frame.Width, frame.Height)
                    ),
                }
            )
        );
        var result = engine.Inspect(request);
        Assert.AreEqual(EInspectionVerdict.Ng, result.Verdict);
        Assert.IsTrue(result.Analysis.Regions[0].Findings.Any(f => f.Code == "barcode_missing_ink"));
    }

    /// <summary>规则字表原子转换为独立大小写敏感条目，不变成有序整串模板。</summary>
    [TestMethod]
    public void SheetImportIsAtomicAndIndependent()
    {
        using var temp = new TempStore();
        string id = temp.Store.CreateLibrary("sheet");
        using var image = new Mat(40, 60, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(image, "Aa", new Point(2, 30), HersheyFonts.HersheySimplex, 1, Scalar.All(0), 2);
        Assert.AreEqual(2, temp.Store.PutSheet(id, 1, Frame(image), "Aa", 1, 2, 0));
        Assert.AreEqual(2, temp.Store.Load(id, 2).Glyphs.Count);
        Assert.ThrowsExactly<ArgumentException>(() => temp.Store.PutSheet(id, 2, Frame(image), "AA", 1, 2));
        Assert.AreEqual(2, temp.Store.Latest(id));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            temp.Store.PutSheet(id, 2, Frame(image), "AB", 1, 2, 32)
        );
        Assert.AreEqual(2, temp.Store.Latest(id));
    }

    /// <summary>可独立替换字符任务，不替换识别、存储或整个检测后台。</summary>
    [TestMethod]
    public void CharacterTaskSeamsAreActuallyUsed()
    {
        using var temp = new TempStore();
        string id = temp.Store.CreateLibrary("regular");
        temp.Store.PutGlyph(id, 1, "A", Glyph());
        var tasks = new CountingCharacterTasks();
        var frame = Glyph();
        using var backend = new OpenCvInspectionBackend(
            libraries: temp.Store,
            segmenter: tasks,
            comparer: tasks
        );
        using var engine = new InspectionEngine(backend);
        engine.Inspect(
            new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "tasks",
                    30,
                    40,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[]
                    {
                        new InspectionRegion(
                            "cell",
                            ERegionKind.Text,
                            new PixelRect(0, 0, 30, 40),
                            field: new FieldSettings(id, 2, "A", equalCells: true)
                        ),
                    }
                )
            )
        );
        Assert.AreEqual(1, tasks.SegmentCalls);
        Assert.AreEqual(1, tasks.CompareCalls);
    }

    private static ImageFrame Glyph()
    {
        using var m = new Mat(40, 30, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(m, "A", new Point(3, 31), HersheyFonts.HersheySimplex, 1, Scalar.All(0), 2);
        return Frame(m);
    }

    private static ImageFrame Frame(Mat mat)
    {
        using var packed = mat.Clone();
        var bytes = new byte[mat.Rows * mat.Cols * mat.Channels()];
        Marshal.Copy(packed.Data, bytes, 0, bytes.Length);
        return new ImageFrame(
            mat.Cols,
            mat.Rows,
            mat.Channels() == 1 ? EImagePixelFormat.Gray8 : EImagePixelFormat.Bgr24,
            bytes
        );
    }
}
