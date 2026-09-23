using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>单个ROI的结果，保留独立测量证据及可选的阶段执行记录。</summary>
public sealed class RegionInspectionResult
{
    /// <summary>创建区域结果，复制证据集合以安全发布，不把读取成功当成质量通过。</summary>
    /// <param name = "regionName">对应的ROI标识。</param>
    /// <param name = "findings">待复制的诊断证据集合。</param>
    /// <param name = "recognition">可选真实OCR证据；未执行时为null。</param>
    /// <param name = "segmentation">物理字符分割和原图归属信息。</param>
    /// <param name = "glyphs">逐字符外观测量记录。</param>
    /// <param name = "barcodes">实际解码得到的码符号记录。</param>
    public RegionInspectionResult(
        string regionName,
        IEnumerable<InspectionFinding> findings,
        TextLineRecognition? recognition = null,
        CharacterSegmentation? segmentation = null,
        IEnumerable<GlyphInspection>? glyphs = null,
        IEnumerable<BarcodeObservation>? barcodes = null
    )
    {
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("Region name is required.", nameof(regionName));
        }

        if (findings == null)
        {
            throw new ArgumentNullException(nameof(findings));
        }

        var copy = findings.ToArray();
        if (copy.Any(f => f == null))
        {
            throw new ArgumentException("Null finding.", nameof(findings));
        }

        RegionName = regionName;
        Findings = new ReadOnlyCollection<InspectionFinding>(copy);
        Recognition = recognition;
        Segmentation = segmentation;
        Glyphs = Array.AsReadOnly((glyphs ?? Array.Empty<GlyphInspection>()).ToArray());
        Barcodes = Array.AsReadOnly((barcodes ?? Array.Empty<BarcodeObservation>()).ToArray());
    }

    /// <summary>分阶段后台的显式执行记录；历史非结构化后台结果中可以不存在。</summary>
    public RoiExecution? Execution { get; private set; }

    /// <summary>附带精确阶段记录发布新结果，不改写原始读取和测量证据。</summary>
    /// <param name = "execution">本ROI的不可变阶段执行记录。</param>
    /// <returns>带执行记录的新区域结果。</returns>
    public RegionInspectionResult WithExecution(RoiExecution execution)
    {
        return new RegionInspectionResult(RegionName, Findings, Recognition, Segmentation, Glyphs, Barcodes)
        {
            Execution = execution ?? throw new ArgumentNullException(nameof(execution)),
        };
    }

    /// <summary>实际物理分割证据。</summary>
    public CharacterSegmentation? Segmentation { get; }

    /// <summary>逐字符比较证据。</summary>
    public IReadOnlyList<GlyphInspection> Glyphs { get; }

    /// <summary>实际解码得到的码符号。</summary>
    public IReadOnlyList<BarcodeObservation> Barcodes { get; }

    /// <summary>真实单行OCR结果；未执行时为null。</summary>
    public TextLineRecognition? Recognition { get; }

    /// <summary>区域标识。</summary>
    public string RegionName { get; }

    /// <summary>不可变诊断证据集合。</summary>
    public IReadOnlyList<InspectionFinding> Findings { get; }
}
