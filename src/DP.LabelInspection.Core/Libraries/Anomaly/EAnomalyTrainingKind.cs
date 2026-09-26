namespace DP.LabelInspection.Core;

/// <summary>批量训练页中一个模型的训练方式，决定样本框的规则和发布的模型类型。</summary>
public enum EAnomalyTrainingKind
{
    /// <summary>整ROI、内容固定（标题、图形、固定文字、空白区、内容不变的码）：位置相关，样本框同尺寸，后加的框自动对齐到第一个样本。</summary>
    FixedContent = 0,

    /// <summary>整ROI、内容可变（序列号条码/QR等）：与位置无关，框的尺寸和位置不限，只需框住内容；只能发现明显污损，码的主判断仍为方法A。</summary>
    VariableContent = 1,

    /// <summary>逐字符文字（内容可变的文字行）：框住整行即可，自动分割为字符，每个字符一个模型。</summary>
    Characters = 2,
}
