using System;
using System.Collections.Generic;

namespace DP.LabelInspection.Runtime;

/// <summary>宿主组装参数：数据目录、可选OCR模型、可选方法B的CNN骨干网络及要安装的种子字库。</summary>
public sealed class LabelInspectionHostOptions
{
    /// <summary>创建参数。</summary>
    /// <param name = "dataRoot">存储根目录（配方、报告、单字库、异常模型库）。</param>
    public LabelInspectionHostOptions(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root required.", nameof(dataRoot));
        }

        DataRoot = dataRoot;
    }

    /// <summary>存储根目录。</summary>
    public string DataRoot { get; }

    /// <summary>OCR识别模型（ONNX）路径；null时不加载OCR，可稍后用<see cref = "LabelInspectionHost.CreateEngine"/>换入。</summary>
    public string? RecognitionModel { get; set; }

    /// <summary>方法B的CNN骨干网络（ONNX）路径；null时训练使用手工特征，已有的两种特征模型都可检测（CNN模型需要此项）。</summary>
    public string? AnomalyBackbone { get; set; }

    /// <summary>
    /// 每次检测最多并行执行的ROI数，默认取处理器数与4中的较小者。宿主只使用内置算法（均线程安全），
    /// 因此创建的引擎可并行执行不从其他ROI取引导值的ROI；设为1即按配方顺序串行。
    /// </summary>
    public int MaximumParallelRois { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    /// <summary>额外厂商异常实现；只登记工厂，原生资产首次使用时加载。配置冻结为宿主快照。</summary>
    public IList<DP.Vision.Algorithms.IAnomalyImplementation> AnomalyImplementations { get; } = new List<DP.Vision.Algorithms.IAnomalyImplementation>();

    /// <summary>启动时安装的种子字库JSON文件（已有同一种子时不重置用户版本）。</summary>
    public IList<string> SeedLibraryFiles { get; } = new List<string>();
}
