using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>宿主组装根：同一存储供多个引擎共用，训练工程往返时接上模板定位，释放后拒绝再创建引擎。</summary>
[TestClass]
public sealed class LabelInspectionHostTests
{
    /// <summary>创建的引擎能检测并使用宿主存储；换引擎不重建存储；训练工程载入后可自动对齐。</summary>
    [TestMethod]
    public void HostComposesEnginesAroundOneStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "dp-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = LabelInspectionHost.Create(new LabelInspectionHostOptions(root));
            Assert.IsNull(host.RecognitionModel);
            var recipe = new InspectionRecipe(
                "host",
                32,
                32,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                new[]
                {
                    new InspectionRegion("blank", ERegionKind.Blank, new PixelRect(4, 4, 16, 16)).WithTasks(
                        new RoiInspectionTasks(false, true)
                    ),
                }
            );
            var image = new ImageFrame(
                32,
                32,
                EImagePixelFormat.Gray8,
                Enumerable.Repeat((byte)255, 1024).ToArray()
            );
            using (var first = host.CreateEngine())
            {
                var report = first.Inspect(TestRequests.FromSnapshot(image, recipe));
                Assert.AreEqual(
                    EInspectionVerdict.Ok,
                    report.Verdict,
                    string.Join(";", report.Analysis.Regions.SelectMany(r => r.Findings).Select(f => f.Code))
                );
                host.Store.SaveReport(TestRequests.FromSnapshot(image, recipe), report);
            }

            using (var second = host.CreateEngine())
            {
                Assert.IsFalse(second.Capabilities.HasFlag(EInspectionCapabilities.Ocr));
                Assert.AreEqual(host.MaximumParallelRois, second.MaximumParallelRois);
                Assert.IsTrue(second.MaximumParallelRois >= 1);
            }

            Assert.AreEqual(1, host.Store.History().Count);

            var session = new AnomalyTrainingSession();
            session.AddImage(image, "good");
            string project = Path.Combine(root, "training.json");
            host.SaveTrainingProject(session, project);
            var (loaded, _) = host.LoadTrainingProject(project, recipe.Regions);
            Assert.AreSame(host.TemplateLocator, loaded.Locator);
            Assert.AreEqual(1, loaded.Images.Count);

            host.Dispose();
            host.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => host.CreateEngine());
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
