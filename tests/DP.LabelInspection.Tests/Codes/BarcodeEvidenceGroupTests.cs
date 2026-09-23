using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Tests;

/// <summary>分组只是无损展示和导出变化，不删除底层缺陷证据。</summary>
[TestClass]
public sealed class BarcodeEvidenceGroupTests
{
    /// <summary>一个条码形成一个F父项，保留全部原始发现及确定的子项标识。</summary>
    [TestMethod]
    [DataRow(EInspectionVerdict.Ng)]
    [DataRow(EInspectionVerdict.Review)]
    public void BarcodeSummaryRetainsEveryChild(EInspectionVerdict verdict)
    {
        var report = Report(verdict);
        var groups = report.EvidenceGroups;
        Assert.AreEqual(2, groups.Count);
        var group = groups[0];
        Assert.AreEqual("F1", group.Id);
        Assert.IsTrue(group.IsBarcode);
        Assert.AreEqual(verdict, group.Summary.Verdict);
        Assert.AreEqual(new PixelRect(10, 10, 80, 50), group.Summary.Bounds!.Value);
        Assert.AreEqual(3, group.Children.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.AreSame(report.Analysis.Regions[0].Findings[i], group.Children[i].Finding);
            Assert.AreEqual("F1." + (i + 1), group.Children[i].Id);
        }

        Assert.AreEqual("F2", groups[1].Id);
        Assert.IsFalse(groups[1].IsBarcode);
        Assert.AreSame(report.Findings[0], groups[1].Summary);
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<InspectionReport>(
            Newtonsoft.Json.JsonConvert.SerializeObject(report)
        )!;
        Assert.AreEqual(2, restored.EvidenceGroups.Count);
        Assert.AreEqual(3, restored.EvidenceGroups[0].Children.Count);
    }

    /// <summary>新分组输出与原平面证据在通过哈希校验的ZIP内共存。</summary>
    [TestMethod]
    public void StoredOutputContainsGroupedAndOriginalRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "dp-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var codec = new OpenCvImageCodec();
            var store = new InspectionStore(root, codec);
            var frame = new ImageFrame(
                100,
                80,
                EImagePixelFormat.Gray8,
                Enumerable.Repeat((byte)255, 8000).ToArray()
            );
            var request = new InspectionRequest(
                frame,
                new InspectionRecipe(
                    "group",
                    100,
                    80,
                    EInspectionMode.Free,
                    EAlignmentMode.AssumeAligned,
                    new[] { new InspectionRegion("bar", ERegionKind.Barcode, new PixelRect(10, 10, 80, 50)) }
                )
            );
            var report = Report(EInspectionVerdict.Ng);
            var id = store.SaveReport(request, report, codec.Annotate(frame, report));
            var path = Path.Combine(root, "groups.zip");
            store.ExportReport(id, path);
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.Single(e => e.FullName == "report.json");
            using var reader = new StreamReader(entry.Open());
            var json = JObject.Parse(reader.ReadToEnd());
            var group = json["evidenceGroups"]![0]!;
            Assert.AreEqual("F1", (string?)group["id"]);
            Assert.AreEqual("NG", (string?)group["status"]);
            Assert.AreEqual(3, group["children"]!.Count());
            Assert.AreEqual(6, (int)group["children"]![1]!["finding"]!["areaPixels"]!);
            Assert.AreEqual(3, json["analysis"]!["regions"]![0]!["findings"]!.Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>主导出图隐藏内部框，原始证据不变。</summary>
    [TestMethod]
    public void ExportedOverviewDoesNotPaintEverySmallDefect()
    {
        var frame = new ImageFrame(
            100,
            80,
            EImagePixelFormat.Gray8,
            Enumerable.Repeat((byte)255, 8000).ToArray()
        );
        var image = new OpenCvImageCodec().Annotate(frame, Report(EInspectionVerdict.Ng));
        var pixels = image.CopyPixels();
        int index = 30 * image.Stride + 30 * 3;
        Assert.AreEqual((byte)255, pixels[index]);
        Assert.AreEqual((byte)255, pixels[index + 1]);
        Assert.AreEqual((byte)255, pixels[index + 2]);
    }

    private static InspectionReport Report(EInspectionVerdict verdict)
    {
        return new InspectionReport(
            "test",
            verdict,
            new BackendAnalysis(
                255,
                200,
                new[]
                {
                    new RegionInspectionResult(
                        "bar",
                        new[]
                        {
                            new InspectionFinding(
                                "barcode_decoded",
                                "code",
                                EInspectionVerdict.Ok,
                                new PixelRect(10, 10, 80, 50)
                            ),
                            new InspectionFinding(
                                "barcode_ink_loss",
                                "loss",
                                verdict,
                                new PixelRect(30, 30, 3, 3),
                                6
                            ),
                            new InspectionFinding(
                                "barcode_extra_ink",
                                "spot",
                                verdict,
                                new PixelRect(50, 30, 3, 3),
                                5
                            ),
                        }
                    ),
                }
            ),
            new[] { new InspectionFinding("scope", "declared scope", EInspectionVerdict.Review) },
            0
        );
    }
}
