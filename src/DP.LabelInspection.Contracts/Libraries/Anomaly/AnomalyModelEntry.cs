using System;
using System.Text.RegularExpressions;
using DP.Vision.Algorithms;

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
    /// <param name = "model">序列化的模型字节，非空，不超过256MiB，完整原生模型及配套文件共同计入。</param>
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
    /// <param name = "scope">适用范围：整个ROI或单个字符；字符模型的键为一个可见Unicode单字（可加字符组前缀）。</param>
    /// <param name = "inkThreshold">字符模型的缺墨阈值（墨量，0–1之间的比例）；null表示未标定，不做缺墨检查。</param>
    /// <param name="normalization">字符模型行归一化方式；历史缺省为墨迹行几何。</param>
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
        EAnomalyModelScope scope = EAnomalyModelScope.Region,
        double? inkThreshold = null,
        ECharacterNormalization normalization = ECharacterNormalization.LineInk
    )
    {
        if (inkThreshold is double ink && (!(ink > 0) || ink > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(inkThreshold));
        }

        if (!Enum.IsDefined(typeof(EAnomalyModelScope), scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        if (
            !Enum.IsDefined(typeof(ECharacterNormalization), normalization)
            || scope == EAnomalyModelScope.Region && normalization != ECharacterNormalization.LineInk
        )
            throw new ArgumentException("模型归一化方式无效或与适用范围不一致。", nameof(normalization));
        if (scope == EAnomalyModelScope.Character && !IsCharacterKey(key))
        {
            throw new ArgumentException(
                "字符模型键须为一个可见Unicode单字，可加字符组前缀；不支持空白或组合序列。",
                nameof(key)
            );
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            throw new ArgumentException("Model key required.", nameof(key));
        }

        if (model == null || model.Length == 0 || model.Length > AnomalyModelAsset.MaximumBytes)
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

        if (AnomalyModelAsset.IsAsset(model))
        {
            var asset = AnomalyModelAsset.FromBytes(model);
            if (asset.ImplementationId != featureSource || asset.Width != width || asset.Height != height || asset.Threshold != threshold)
                throw new ArgumentException("完整模型资产与库条目实现/输入尺寸/阈值不一致。", nameof(model));
        }
        else if (model.Length > 64 * 1024 * 1024)
            throw new ArgumentException("历史DPPA模型仍限制64MiB；较大原生模型必须使用版本化完整资产包。", nameof(model));
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
        InkThreshold = inkThreshold;
        Normalization = normalization;
    }

    /// <summary>
    /// 缺墨阈值：笔画内墨量比任何良品在±1像素内的最低墨量还低多少（墨量为按纸色归一化的0–1比例，3×3平均）即判缺墨；
    /// 按来源图留一标定。null表示未标定（旧版本模型或与位置无关的模型），不做缺墨检查。
    /// </summary>
    public double? InkThreshold { get; }

    /// <summary>字符模型训练时行归一化方式，历史模型为墨迹行几何。</summary>
    public ECharacterNormalization Normalization { get; }

    /// <summary>适用范围：整个ROI或单个字符。</summary>
    public EAnomalyModelScope Scope { get; }

    /// <summary>字符模型对应的Unicode单字（不拆代理对）；整ROI模型为null。</summary>
    public string? Character =>
        Scope == EAnomalyModelScope.Character && ParseCharacterKey(Key, out _, out var character)
            ? character
            : null;

    /// <summary>
    /// 字符模型所属的字符组（键“组/字符”中的组，通常为文字ROI名称或同一字体的几行共用的名称）；未分组的字符模型为null，整ROI模型为null。
    /// 不同字体、字号的行分组训练，阈值按本组样本标定，不被其他字体的同一字符抬高。
    /// </summary>
    public string? Group =>
        Scope == EAnomalyModelScope.Character && ParseCharacterKey(Key, out var group, out _) ? group : null;

    /// <summary>字符模型的键：“字符”或“组/字符”。</summary>
    /// <param name = "group">字符组；null表示不分组。</param>
    /// <param name = "character">一个可见Unicode单字，支持中文、标点及补充平面。</param>
    public static string CharacterKey(string? group, string character)
    {
        if (group != null && !IsCharacterGroup(group))
        {
            throw new ArgumentException("字符分组须为1–60个字符，不能为空或包含“/”。", nameof(group));
        }
        if (!CharacterIdentity.IsGlyph(character))
            throw new ArgumentException(
                "异常模型身份须为一个可见Unicode单字（中文、字母、数字、标点或符号），不支持空白或组合序列。",
                nameof(character)
            );
        return group == null ? character : group + "/" + character;
    }

    /// <summary>字符组名称是否可用：1–60字符，不含“/”。</summary>
    /// <param name = "group">字符组名称。</param>
    public static bool IsCharacterGroup(string? group)
    {
        return !string.IsNullOrWhiteSpace(group) && group!.Length <= 60 && group.IndexOf('/') < 0;
    }

    private static bool IsCharacterKey(string? key) => ParseCharacterKey(key, out _, out _);

    private static bool ParseCharacterKey(string? key, out string? group, out string character)
    {
        group = null;
        character = "";
        if (string.IsNullOrEmpty(key))
            return false;
        int length =
            key!.Length >= 2
            && char.IsHighSurrogate(key[key.Length - 2])
            && char.IsLowSurrogate(key[key.Length - 1])
                ? 2
                : 1;
        character = key.Substring(key.Length - length);
        if (!CharacterIdentity.IsGlyph(character))
            return false;
        if (key.Length == length)
            return true;
        int delimiter = key.Length - length - 1;
        if (key[delimiter] != '/')
            return false;
        group = key.Substring(0, delimiter);
        return IsCharacterGroup(group);
    }

    /// <summary>库内模型键。</summary>
    public string Key { get; }

    /// <summary>模型字节的SHA256。</summary>
    public string Sha256 { get; }

    /// <summary>特征来源。</summary>
    public string FeatureSource { get; }

    /// <summary>真实实现名称，厂商原生资产不再误显示为“手工”。</summary>
    public string ImplementationDisplay => FeatureSource == "handcrafted" ? "手工Patch"
        : FeatureSource.StartsWith("cnn", StringComparison.Ordinal) ? "CNN Patch" : FeatureSource;

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
            Scope,
            InkThreshold,
            Normalization
        );
    }

    /// <summary>供用户阅读的摘要。</summary>
    public override string ToString()
    {
        return (Scope == EAnomalyModelScope.Character ? "字符[" + Key + "]" : Key)
            + " · "
            + ImplementationDisplay
            + (LocalRadius > 0 ? "/位置相关" : "/与位置无关")
            + " · "
            + TrainingImages
            + (Scope == EAnomalyModelScope.Character ? "个良品样本 · " : "张良品 · ")
            + Width
            + "×"
            + Height;
    }
}
