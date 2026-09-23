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

    /// <summary>真实识别、解码和候选实现程序集不包含标签依赖。</summary>
    [TestMethod]
    public void NeutralImplementationsDoNotReferenceLabelBusiness()
    {
        var assemblies = new[]
        {
            typeof(DP.Vision.Onnx.OnnxTextLineRecognizer).Assembly,
            typeof(DP.Vision.Zxing.ZxingBarcodeDecoder).Assembly,
            typeof(DP.Vision.OnnxDetection.OnnxTextRegionDetector).Assembly,
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
    }
}
