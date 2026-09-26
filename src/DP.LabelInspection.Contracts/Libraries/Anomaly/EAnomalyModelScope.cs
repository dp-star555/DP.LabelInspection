namespace DP.LabelInspection.Contracts;

/// <summary>异常模型（质量方法B）的适用范围，按数值保存在模型库中。</summary>
public enum EAnomalyModelScope
{
    /// <summary>整个ROI一个模型，模型键通常为ROI名称。</summary>
    Region = 0,

    /// <summary>一个字符一个模型，模型键为该字符（ASCII字母或数字，区分大小写），用于内容可变的文字ROI。</summary>
    Character = 1,
}
