using System;
using System.Text.RegularExpressions;

namespace DP.LabelInspection.Contracts;

/// <summary>ROI绑定的固定版本异常模型（方法B），与字库绑定方式一致：类别标识+精确版本+模型键，不自动升级到最新版本。</summary>
public sealed class AnomalySettings
{
    /// <summary>创建固定版本绑定。</summary>
    /// <param name = "libraryId">异常模型库标识。</param>
    /// <param name = "libraryRevision">精确不可变版本号，至少1。</param>
    /// <param name = "modelKey">库内模型键；为null时使用ROI名称。</param>
    public AnomalySettings(string libraryId, int libraryRevision, string? modelKey = null)
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

        LibraryId = libraryId;
        LibraryRevision = libraryRevision;
        ModelKey = modelKey;
    }

    /// <summary>异常模型库标识。</summary>
    public string LibraryId { get; }

    /// <summary>精确不可变版本号。</summary>
    public int LibraryRevision { get; }

    /// <summary>库内模型键；null表示使用ROI名称。</summary>
    public string? ModelKey { get; }

    /// <summary>该ROI实际使用的模型键。</summary>
    /// <param name = "regionName">ROI名称。</param>
    public string KeyFor(string regionName)
    {
        return ModelKey ?? regionName;
    }
}
