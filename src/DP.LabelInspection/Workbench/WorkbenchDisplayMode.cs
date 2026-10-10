namespace DP.LabelInspection;

/// <summary>工作台画布显示的内容。</summary>
public enum WorkbenchDisplayMode
{
    /// <summary>只显示输入图像，不叠加ROI与结果，也不能编辑ROI。</summary>
    InputImage,

    /// <summary>输入图像叠加ROI，可选中、移动、缩放及新建ROI。</summary>
    Regions,

    /// <summary>叠加ROI、检测证据与单字框；尚无结果时与<see cref="Regions"/>相同。</summary>
    Result,
}
