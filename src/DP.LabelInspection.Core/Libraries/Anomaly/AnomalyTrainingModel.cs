using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>批量训练页中的一个模型：名称即模型键，可对应一个配方ROI（发布后可一键绑定）。</summary>
public sealed class AnomalyTrainingModel
{
    internal AnomalyTrainingModel(string name, EAnomalyTrainingKind kind, InspectionRegion? region)
    {
        Name = name;
        Kind = kind;
        Region = region;
        CharacterGroup = AnomalyModelEntry.IsCharacterGroup(name) ? name : "文字";
    }

    /// <summary>模型名称（模型键）；对应配方ROI时与ROI同名。</summary>
    public string Name { get; }

    /// <summary>训练方式。</summary>
    public EAnomalyTrainingKind Kind { get; internal set; }

    /// <summary>对应的配方ROI；没有时只发布模型，不参与绑定。</summary>
    public InspectionRegion? Region { get; internal set; }

    /// <summary>内容固定模型的样本框尺寸（第一个样本或对应ROI决定）；其他方式为null。</summary>
    public int? Width { get; internal set; }

    /// <summary>内容固定模型的样本框高度。</summary>
    public int? Height { get; internal set; }

    /// <summary>
    /// 逐字符模型的字符组，默认与模型名称相同（每个文字行单独一组）。同一字体、字号的几行可填相同的组共用样本；
    /// 不同字体混在一组会使同一字符的良品差异变大，阈值被抬高，缺陷不易检出。其他训练方式不使用。
    /// </summary>
    public string CharacterGroup { get; internal set; } = "";

    /// <summary>显示名称。</summary>
    public override string ToString()
    {
        return Name;
    }
}
