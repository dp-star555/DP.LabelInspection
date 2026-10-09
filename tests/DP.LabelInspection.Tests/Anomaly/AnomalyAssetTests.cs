using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using DP.Vision;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>模型资产公开契约：原生字节不经Patch解析，元数据和配套文件原子往返。</summary>
[TestClass]
public sealed class AnomalyAssetTests
{
    /// <summary>摘要篡改和尾随数据不能伪装有效原生资产。</summary>
    [TestMethod]
    public void TamperingAndTrailingDataAreRejected()
    {
        var bytes = Asset("native.a").ToBytes(); bytes[bytes.Length - 1] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => AnomalyModelAsset.FromBytes(bytes));
        Assert.ThrowsExactly<InvalidDataException>(() => AnomalyModelAsset.FromBytes(Asset("native.a").ToBytes().Concat(new byte[] { 1 }).ToArray()));
    }

    /// <summary>同一业务引擎固定修订A→B→A，结果来自各自原生实例，无Patch解析和隐式回退；关闭释放实例。</summary>
    [TestMethod]
    public void FormalInspectionSwitchesImplementationsAndDisposesRuntimes()
    {
        string root = Path.Combine(Path.GetTempPath(), "anomaly-providers-" + Guid.NewGuid().ToString("N"));
        var a = new Implementation("native.a", .1f); var b = new Implementation("native.b", .8f);
        try
        {
            var store = new InspectionStore(root, new OpenCvImageCodec());
            string id = store.AnomalyLibraries.CreateAnomalyLibrary("native assets");
            store.AnomalyLibraries.PutAnomalyModels(id, 1, new[] { Entry(Asset(a.ImplementationId)) });
            store.AnomalyLibraries.PutAnomalyModels(id, 2, new[] { Entry(Asset(b.ImplementationId)) }, true);
            Assert.AreEqual(AnomalyLibraryStore.AssetSchema, (string?)store.AnomalyLibraries.Read(id, 3)["schema"]);
            using (var engine = new InspectionEngine(new OpenCvInspectionBackend(anomalyModels: store.AnomalyLibraries,
                anomalyImplementations: new[] { a, b }), true))
            {
                var frame = new PixelSnapshot(32, 40, EImagePixelFormat.Gray8, Enumerable.Repeat((byte)240, 1280).ToArray());
                EInspectionVerdict Run(int revision)
                {
                    var roi = new InspectionRegion("native", ERegionKind.Text, new PixelRect(0, 0, 32, 40), true)
                        .WithAnomaly(new AnomalySettings(id, revision)).WithTasks(new RoiInspectionTasks(false, false, true));
                    var recipe = new InspectionRecipe("native", 32, 40, EInspectionMode.Free, EAlignmentMode.AssumeAligned, new[] { roi });
                    return engine.Inspect(TestRequests.FromSnapshot(frame, recipe)).Verdict;
                }
                Assert.AreEqual(EInspectionVerdict.Ok, Run(2)); Assert.AreEqual(EInspectionVerdict.Ng, Run(3)); Assert.AreEqual(EInspectionVerdict.Ok, Run(2));
                Assert.AreEqual(1, a.Loads); Assert.AreEqual(1, b.Loads); Assert.AreEqual(0, a.Disposals);
            }
            Assert.AreEqual(1, a.Disposals); Assert.AreEqual(1, b.Disposals);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static AnomalyModelAsset Asset(string id) => new AnomalyModelAsset(id, "native.v1", 32, 40, .3, 3, "independent-source",
        new Dictionary<string, byte[]> { ["model.bin"] = new byte[] { 1, 2, 3, 4 } });
    private static AnomalyModelEntry Entry(AnomalyModelAsset asset)
    {
        var bytes = asset.ToBytes(); using var sha = SHA256.Create(); string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new AnomalyModelEntry("native", bytes, hash, asset.ImplementationId, asset.Width, asset.Height, 0, 3, asset.Threshold, 0, 4, 1);
    }
    private sealed class Implementation(string id, float score) : IAnomalyImplementation
    {
        internal int Loads, Disposals;
        public string ImplementationId => id; public string DisplayName => id;
        public ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default) { Loads++; return new Runtime(this, asset, score); }
        private sealed class Runtime(Implementation owner, AnomalyModelAsset asset, float score) : ILoadedAnomalyModel
        {
            public AnomalyModelAsset Asset => asset;
            public PatchAnomalyResult Inspect(IImageSource image, AnomalyDetectionOptions options, CancellationToken token = default)
                => AnomalyScoreMap.Measure(Enumerable.Repeat(score, asset.Width * asset.Height).ToArray(), asset.Width, asset.Height,
                    options.Threshold ?? asset.Threshold, options.MinimumArea, asset.ImplementationId);
            public void Dispose() => owner.Disposals++;
        }
    }
    /// <summary>厂商原生模型和预处理一起保存，调用者不能修改已捕获快照。</summary>
    [TestMethod]
    public void NativeModelAndPreprocessingRoundTripWithoutPatchInterpretation()
    {
        var native = new byte[] { 1, 2, 3, 4 };
        var asset = new AnomalyModelAsset("test.native", "native.v1", 32, 40, .3, 3, "source-held-out",
            new Dictionary<string, byte[]> { ["model.bin"] = native, ["preprocess.bin"] = new byte[] { 9, 8 } },
            new Dictionary<string, string> { ["normalization"] = "stable-line-roi" });
        native[0] = 99;
        var read = AnomalyModelAsset.FromBytes(asset.ToBytes());
        Assert.AreEqual("test.native", read.ImplementationId);
        Assert.AreEqual("native.v1", read.ModelFormat);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, read.Read("model.bin"));
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, read.Read("preprocess.bin"));
        Assert.AreEqual("stable-line-roi", read.Settings["normalization"]);
        Assert.ThrowsExactly<ArgumentException>(() => new AnomalyModelAsset("x", "v1", 32, 40, .3, 3, "",
            new Dictionary<string, byte[]> { ["../escape"] = native }));
    }
}
