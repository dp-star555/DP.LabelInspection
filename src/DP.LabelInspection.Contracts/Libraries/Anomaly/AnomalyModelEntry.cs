using System;
using System.Text.RegularExpressions;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 异常模型库中的一个模型（方法B）：不透明的模型字节及训练时记录的元数据。
/// 模型字节由运行时算法解析；元数据用于在不解析模型的情况下显示、校验及重建检测参数。
/// </summary>
public sealed class AnomalyModelEntry
{
    private readonly byte[] _model;

    /// <summary>创建模型条目，复制模型字节。</summary>
    /// <param name = "key">库内模型键，通常为ROI名称，1–100字符。</param>
    /// <param name = "model">序列化的模型字节，非空，不超过64MB。</param>
    /// <param name = "sha256">模型字节的小写SHA256，须与字节一致。</param>
    /// <param name = "featureSource">特征来源（handcrafted或cnn:哈希@尺度），检测时须有同来源的实现。</param>
    /// <param name = "width">训练裁图宽度（像素）；位置相关模型要求检测裁图同尺寸。</param>
    /// <param name = "height">训练裁图高度（像素）。</param>
    /// <param name = "localRadius">位置相关模式的搜索半径；0表示与位置无关。</param>
    /// <param name = "trainingImages">训练用良品图数量。</param>
    /// <param name = "threshold">训练标定的得分阈值。</param>
    /// <param name = "margin">训练时ROI四周额外裁取的原图像素，检测须一致。</param>
    /// <param name = "stride">检测块采样步长。</param>
    /// <param name = "minimumArea">异常区域最小面积（原图平方像素）。</param>
    /// <param name = "calibration">阈值标定说明。</param>
    /// <param name = "scope">适用范围：整个ROI或单个字符；字符模型的键必须是单个ASCII字母或数字。</param>
    public AnomalyModelEntry(
        string key,
        byte[] model,
        string sha256,
        string featureSource,
        int width,
        int height,
        int localRadius,
        int trainingImages,
        double threshold,
        int margin,
        int stride,
        int minimumArea,
        string calibration = "",
        EAnomalyModelScope scope = EAnomalyModelScope.Region
    )
    {
        if (!Enum.IsDefined(typeof(EAnomalyModelScope), scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        if (
            scope == EAnomalyModelScope.Character
            && (key == null || key.Length != 1 || !FieldSettings.IsAlphanumeric(key[0]))
        )
        {
            throw new ArgumentException(
                "Character models are keyed by one ASCII letter or digit.",
                nameof(key)
            );
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            throw new ArgumentException("Model key required.", nameof(key));
        }

        if (model == null || model.Length == 0 || model.Length > 64 * 1024 * 1024)
        {
            throw new ArgumentException("Invalid model bytes.", nameof(model));
        }

        if (
            sha256 == null
            || !Regex.IsMatch(
                sha256,
                "^[0-9a-f]{64}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            )
        )
        {
            throw new ArgumentException("Invalid model hash.", nameof(sha256));
        }

        if (string.IsNullOrWhiteSpace(featureSource) || featureSource.Length > 200)
        {
            throw new ArgumentException("Feature source required.", nameof(featureSource));
        }

        if (
            width < 1
            || height < 1
            || localRadius < 0
            || trainingImages < 1
            || !(threshold > 0)
            || double.IsInfinity(threshold)
            || margin < 0
            || margin > 256
            || stride < 1
            || stride > 16
            || minimumArea < 1
        )
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Invalid anomaly model metadata.");
        }

        Key = key;
        _model = (byte[])model.Clone();
        Sha256 = sha256;
        FeatureSource = featureSource;
        Width = width;
        Height = height;
        LocalRadius = localRadius;
        TrainingImages = trainingImages;
        Threshold = threshold;
        Margin = margin;
        Stride = stride;
        MinimumArea = minimumArea;
        Calibration = calibration ?? "";
        Scope = scope;
    }

    /// <summary>适用范围：整个ROI或单个字符。</summary>
    public EAnomalyModelScope Scope { get; }

    /// <summary>库内模型键。</summary>
    public string Key { get; }

    /// <summary>模型字节的SHA256。</summary>
    public string Sha256 { get; }

    /// <summary>特征来源。</summary>
    public string FeatureSource { get; }

    /// <summary>训练裁图宽度。</summary>
    public int Width { get; }

    /// <summary>训练裁图高度。</summary>
    public int Height { get; }

    /// <summary>位置相关搜索半径；0为与位置无关。</summary>
    public int LocalRadius { get; }

    /// <summary>训练良品图数量。</summary>
    public int TrainingImages { get; }

    /// <summary>标定阈值。</summary>
    public double Threshold { get; }

    /// <summary>ROI四周额外裁取的原图像素。</summary>
    public int Margin { get; }

    /// <summary>检测块采样步长。</summary>
    public int Stride { get; }

    /// <summary>异常区域最小面积。</summary>
    public int MinimumArea { get; }

    /// <summary>阈值标定说明。</summary>
    public string Calibration { get; }

    /// <summary>模型字节长度。</summary>
    public int Length => _model.Length;

    /// <summary>返回模型字节副本。</summary>
    public byte[] CopyModel()
    {
        return (byte[])_model.Clone();
    }

    /// <summary>复制条目并使用新键，模型与元数据不变。</summary>
    /// <param name = "key">新的模型键。</param>
    public AnomalyModelEntry WithKey(string key)
    {
        return new AnomalyModelEntry(
            key,
            _model,
            Sha256,
            FeatureSource,
            Width,
            Height,
            LocalRadius,
            TrainingImages,
            Threshold,
            Margin,
            Stride,
            MinimumArea,
            Calibration,
            Scope
        );
    }

    /// <summary>供用户阅读的摘要。</summary>
    public override string ToString()
    {
        return (Scope == EAnomalyModelScope.Character ? "字符[" + Key + "]" : Key)
            + " · "
            + (FeatureSource.StartsWith("cnn", StringComparison.Ordinal) ? "CNN" : "手工")
            + (LocalRadius > 0 ? "/位置相关" : "/与位置无关")
            + " · "
            + TrainingImages
            + (Scope == EAnomalyModelScope.Character ? "个良品样本 · " : "张良品 · ")
            + Width
            + "×"
            + Height;
    }
}
