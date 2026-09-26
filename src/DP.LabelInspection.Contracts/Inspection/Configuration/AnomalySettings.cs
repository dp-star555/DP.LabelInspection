using System;
using System.Text.RegularExpressions;

namespace DP.LabelInspection.Contracts;

/// <summary>ROI绑定的固定版本异常模型（方法B），与字库绑定方式一致：类别标识+精确版本+模型键，不自动升级到最新版本。</summary>
public sealed class AnomalySettings
{
    /// <summary>创建固定版本绑定。</summary>
    /// <param name = "libraryId">异常模型库标识。</param>
    /// <param name = "libraryRevision">精确不可变版本号，至少1。</param>
    /// <param name = "modelKey">
    /// 整ROI模式：库内模型键，null时使用ROI名称。逐字符模式：字符组（通常为ROI名称），null表示使用未分组的字符模型。
    /// </param>
    /// <param name = "perCharacter">
    /// 逐字符模式：按文字质量的分割结果逐字检查，每个字符使用库中同名的字符模型（适合内容可变的文字）；仅文字ROI可用。
    /// </param>
    public AnomalySettings(
        string libraryId,
        int libraryRevision,
        string? modelKey = null,
        bool perCharacter = false
    )
    {
        if (
            string.IsNullOrWhiteSpace(libraryId)
            || !Regex.IsMatch(
                libraryId,
                "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            )
        )
        {
            throw new ArgumentException("Invalid anomaly library id.", nameof(libraryId));
        }

        if (libraryRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryRevision));
        }

        if (modelKey != null && (modelKey.Trim().Length == 0 || modelKey.Length > 100))
        {
            throw new ArgumentException("Invalid model key.", nameof(modelKey));
        }

        if (perCharacter && modelKey != null && !AnomalyModelEntry.IsCharacterGroup(modelKey))
        {
            throw new ArgumentException("Invalid character group.", nameof(modelKey));
        }

        LibraryId = libraryId;
        LibraryRevision = libraryRevision;
        ModelKey = modelKey;
        PerCharacter = perCharacter;
    }

    /// <summary>逐字符模式：每个分割出的字符使用库中该字符的模型。</summary>
    public bool PerCharacter { get; }

    /// <summary>异常模型库标识。</summary>
    public string LibraryId { get; }

    /// <summary>精确不可变版本号。</summary>
    public int LibraryRevision { get; }

    /// <summary>整ROI模式为库内模型键（null表示ROI名称）；逐字符模式为字符组（null表示未分组的字符模型）。</summary>
    public string? ModelKey { get; }

    /// <summary>该ROI实际使用的模型键。</summary>
    /// <param name = "regionName">ROI名称。</param>
    public string KeyFor(string regionName)
    {
        return ModelKey ?? regionName;
    }
}
