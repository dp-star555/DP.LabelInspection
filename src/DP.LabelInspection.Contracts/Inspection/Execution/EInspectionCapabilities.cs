using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>高层后台能力，不是跨库基础算子的全集枚举。</summary>
[Flags]
public enum EInspectionCapabilities
{
    /// <summary>不具备任何能力。</summary>
    None = 0,

    /// <summary>图像质量评估。</summary>
    Quality = 1,

    /// <summary>固定区域缺墨和多墨检查。</summary>
    FixedDifference = 2,

    /// <summary>空白区域墨迹检查。</summary>
    BlankSpots = 4,

    /// <summary>有界图像配准。</summary>
    TranslationAlignment = 8,

    /// <summary>真实文本推理。</summary>
    Ocr = 16,

    /// <summary>基于图像像素归属的字符分割。</summary>
    CharacterSegmentation = 32,

    /// <summary>独立字形参考比较。</summary>
    GlyphComparison = 64,

    /// <summary>真实条码解码。</summary>
    BarcodeDecode = 128,

    /// <summary>探索性的条码印刷结构检查。</summary>
    BarcodeStructure = 256,

    /// <summary>自由模式下发现未配置候选，不代表整张标签通过。</summary>
    Discovery = 512,
}
