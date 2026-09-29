using System;
using System.Linq;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>删除旧厂商命名标签项目后的程序集边界测试。</summary>
[TestClass]
public sealed class ProjectBoundaryTests
{
    /// <summary>Runtime既不引用已删除程序集，也不向其转发类型。</summary>
    [TestMethod]
    public void RuntimeDoesNotDependOnRemovedProjects()
    {
        var assembly = typeof(OpenCvInspectionBackend).Assembly;
        Assert.AreEqual("DP.LabelInspection.Runtime", assembly.GetName().Name);
        var removed = new[]
        {
            "DP.LabelInspection.Vision.OpenCv",
            "DP.LabelInspection.Vision.OnnxDetection",
            "DP.LabelInspection.Ocr.Onnx",
            "DP.LabelInspection.Barcode.Zxing",
        };
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => removed.Contains(a.Name)));
        foreach (
            var backend in new[]
            {
                assembly,
                typeof(DP.LabelInspection.Core.InspectionEngine).Assembly,
                typeof(DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter).Assembly,
                typeof(DP.Vision.Algorithms.ICharacterSegmenter).Assembly,
            }
        )
        {
            Assert.IsFalse(
                backend.GetReferencedAssemblies().Any(a => a.Name == "DP.Vision.UI"),
                backend.FullName
            );
        }
#if NET8_0_OR_GREATER
        Assert.AreEqual(0, assembly.GetForwardedTypes().Length);
#endif
    }

    /// <summary>通用读码、OCR、定位、分割及比较契约只由Vision提供，禁止标签侧重新引入镜像接口。</summary>
    [TestMethod]
    public void GeneralVisionAlgorithmsHaveNoLabelContractDuplicates()
    {
        var contracts = typeof(DP.LabelInspection.Contracts.InspectionRequest).Assembly;
        var runtime = typeof(OpenCvInspectionBackend).Assembly;
        var obsolete = new[] {
            "IBarcodeDecoder", "ITextLineRecognizer", "ITextLinePreprocessor",
            "ITextRegionDetector", "ICharacterSegmenter", "IGlyphCandidateSegmenter",
            "IGlyphComparer", "ZxingBarcodeDecoder", "OnnxTextLineRecognizer",
            "OpenCvTextLinePreprocessor", "TextRegionDetector", "CharacterSegmenter", "GlyphComparer",
            // PP-OCR模型输入与概率图证据是Vision执行端的类型，标签侧不得镜像。
            "PPOcrDetectionInput", "PPOcrDetectionOutput", "PPOcrDetectionTask"
        };
        foreach (var assembly in new[] { contracts, runtime })
        {
            foreach (var name in obsolete)
                Assert.IsFalse(assembly.GetTypes().Any(t => t.Name == name), assembly.GetName().Name + ": " + name);
        }
    }

    /// <summary>PP-OCR概率图证据只住在Vision执行端，不进入标签契约面。</summary>
    [TestMethod]
    public void PpocrEvidenceTypesStayOutOfLabelContracts()
    {
        var contracts = typeof(DP.LabelInspection.Contracts.InspectionRequest).Assembly;
        Assert.IsFalse(
            contracts.GetReferencedAssemblies().Any(a => a.Name == "DP.Vision.PPOcr.Onnx"),
            contracts.FullName
        );
        Assert.AreEqual(
            "DP.Vision.PPOcr.Onnx",
            typeof(DP.Vision.PPOcr.Onnx.PPOcrDetectionOutput).Assembly.GetName().Name
        );
    }

    /// <summary>真实识别、检测、解码和候选实现程序集不包含标签依赖；PP-OCR执行端也不携带OpenCV。</summary>
    [TestMethod]
    public void NeutralImplementationsDoNotReferenceLabelBusiness()
    {
        var assemblies = new[]
        {
            typeof(DP.Vision.PPOcr.Onnx.OnnxTextLineRecognizer).Assembly,
            typeof(DP.Vision.Zxing.ZxingBarcodeDecoder).Assembly,
            typeof(DP.Vision.OpenCv.OpenCvGlyphComparer).Assembly,
        };
        foreach (var assembly in assemblies)
        {
            Assert.IsFalse(
                assembly
                    .GetReferencedAssemblies()
                    .Any(a => a.Name!.StartsWith("DP.LabelInspection", StringComparison.Ordinal)),
                assembly.FullName
            );
        }

        // PP-OCR执行端只负责「准备好的输入 → 模型输出证据」：既不含标签依赖，也不含OpenCV。
        var execution = typeof(DP.Vision.PPOcr.Onnx.PPOcrDetectionTask).Assembly;
        Assert.AreEqual("DP.Vision.PPOcr.Onnx", execution.GetName().Name);
        Assert.IsFalse(
            execution.GetReferencedAssemblies().Any(a => a.Name!.Contains("OpenCv")),
            execution.FullName
        );
    }
}
