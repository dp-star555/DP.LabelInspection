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

    /// <summary>启动时安装的种子字库JSON文件（已有同一种子时不重置用户版本）。</summary>
    public IList<string> SeedLibraryFiles { get; } = new List<string>();
}
