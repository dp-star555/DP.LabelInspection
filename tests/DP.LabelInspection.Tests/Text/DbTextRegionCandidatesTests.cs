using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using DP.LabelInspection.Runtime;
using DP.Vision.Algorithms;
using DP.Vision.OnnxDetection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Tests;

/// <summary>DB候选提取的离线回归：只消费阶段1冻结的概率图，不启动ONNX运行时。</summary>
[TestClass]
public sealed class DbTextRegionCandidatesTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Text",
        "Fixtures",
        "PpocrDetection"
    );

    /// <summary>冻结概率图必须逐字节等于真实模型当时的输出，否则回归失去意义。</summary>
    [TestMethod]
    public void FrozenProbabilityMapMatchesItsRecordedHash()
    {
        var fixture = LoadFixture();
        using var sha = SHA256.Create();
        string actual = BitConverter.ToString(sha.ComputeHash(fixture.Raw)).Replace("-", "").ToLowerInvariant();
        Assert.AreEqual(fixture.ProbabilitySha256, actual);
        Assert.AreEqual(fixture.ProbabilityCount * sizeof(float), fixture.Raw.Length);
    }

    /// <summary>离线重放候选提取必须得到真实模型当时的候选数量、坐标与顺序。</summary>
    [TestMethod]
    public void FrozenProbabilityMapReproducesTheRealModelCandidates()
    {
        var fixture = LoadFixture();
        var candidates = DbTextRegionCandidates.Extract(fixture.Evidence);
        Assert.AreEqual(
            string.Join(",", fixture.ExpectedCandidates.Select(b => b.ToString())),
            string.Join(",", candidates.Select(b => b.ToString()))
        );
    }

    /// <summary>概率图尺寸与原图几何来自同一次推理，不能被分别替换。</summary>
    [TestMethod]
    public void FrozenEvidenceKeepsTheInferenceGeometry()
    {
        var fixture = LoadFixture();
        Assert.AreEqual(fixture.MapWidth, fixture.Evidence.Width);
        Assert.AreEqual(fixture.MapHeight, fixture.Evidence.Height);
        Assert.AreEqual(fixture.ImageWidth, fixture.Evidence.ImageWidth);
        Assert.AreEqual(fixture.ImageHeight, fixture.Evidence.ImageHeight);
        Assert.AreEqual(fixture.ModelSha256, fixture.Evidence.ModelSha256);
        Assert.IsTrue(fixture.MapWidth < fixture.ImageWidth);
        Assert.IsTrue(fixture.MapHeight < fixture.ImageHeight);
    }

    /// <summary>候选按先Y后X排序，且数量不超过业务上限。</summary>
    [TestMethod]
    public void CandidatesStayOrderedAndBounded()
    {
        var fixture = LoadFixture();
        var candidates = DbTextRegionCandidates.Extract(fixture.Evidence);
        Assert.IsTrue(candidates.Count > 0 && candidates.Count <= DbTextRegionCandidates.MaxCandidates);
        for (int i = 1; i < candidates.Count; i++)
        {
            bool ordered =
                candidates[i].Y > candidates[i - 1].Y
                || (candidates[i].Y == candidates[i - 1].Y && candidates[i].X > candidates[i - 1].X);
            Assert.IsTrue(ordered, "候选顺序改变：" + candidates[i - 1] + " -> " + candidates[i]);
        }
    }

    private static Fixture LoadFixture()
    {
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "evidence.json")));
        string probabilityFile = (string?)meta["probability_file"]
            ?? throw new InvalidOperationException("缺少概率图文件名。");
        byte[] raw = ReadDecompressed(Path.Combine(FixtureDirectory, probabilityFile));
        int width = (int?)meta["map_width"] ?? throw new InvalidOperationException("缺少概率图宽度。");
        int height = (int?)meta["map_height"] ?? throw new InvalidOperationException("缺少概率图高度。");
        var values = new float[raw.Length / sizeof(float)];
        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
        var evidence = new PPOcrDetectionOutput(
            (string?)meta["detection_model_sha256"] ?? throw new InvalidOperationException("缺少模型身份。"),
            width,
            height,
            (int?)meta["image_width"] ?? throw new InvalidOperationException("缺少原图宽度。"),
            (int?)meta["image_height"] ?? throw new InvalidOperationException("缺少原图高度。"),
            values
        );
        return new Fixture
        {
            Raw = raw,
            Evidence = evidence,
            MapWidth = width,
            MapHeight = height,
            ImageWidth = evidence.ImageWidth,
            ImageHeight = evidence.ImageHeight,
            ModelSha256 = evidence.ModelSha256,
            ProbabilityCount = (int?)meta["probability_count"]
                ?? throw new InvalidOperationException("缺少概率值个数。"),
            ProbabilitySha256 = (string?)meta["probability_sha256"]
                ?? throw new InvalidOperationException("缺少概率图哈希。"),
            ExpectedCandidates = ((JArray?)meta["expected_candidates"])
                ?.Select(v => new PixelBounds((int)v![0]!, (int)v[1]!, (int)v[2]!, (int)v[3]!))
                .ToArray()
                ?? throw new InvalidOperationException("缺少期望候选。"),
        };
    }

    private static byte[] ReadDecompressed(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private sealed class Fixture
    {
        public byte[] Raw { get; set; } = Array.Empty<byte>();
        public PPOcrDetectionOutput Evidence { get; set; } = null!;
        public int MapWidth { get; set; }
        public int MapHeight { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public string ModelSha256 { get; set; } = string.Empty;
        public int ProbabilityCount { get; set; }
        public string ProbabilitySha256 { get; set; } = string.Empty;
        public IReadOnlyList<PixelBounds> ExpectedCandidates { get; set; } = Array.Empty<PixelBounds>();
    }
}
