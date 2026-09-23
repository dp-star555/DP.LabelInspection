using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>不可变字段规则及固定版本的单字符参考绑定。</summary>
public sealed class FieldSettings
{
    /// <summary>创建字段设置；Expected来自调用方真值，不由OCR填写；EqualCells明确声明固定等宽单元。</summary>
    /// <param name = "libraryId">可选字库标识，必须与版本号成对提供。</param>
    /// <param name = "libraryRevision">固定的正整数参考版本，不自动使用最新版本。</param>
    /// <param name = "expected">调用方独立提供的预期内容，最多1024字符；等格布局最多128个ASCII字母数字。</param>
    /// <param name = "pattern">可选全匹配正则表达式，最多512字符，执行带时间限制。</param>
    /// <param name = "allowedCharacters">可选允许字符集合，最多1024字符。</param>
    /// <param name = "minimumLength">最小文本长度，非负。</param>
    /// <param name = "maximumLength">最大文本长度，不小于最小值且不超过1024。</param>
    /// <param name = "equalCells">是否明确声明固定等宽单元，不从OCR推断。</param>
    /// <param name = "maximumDifference">归一化差异比上限，范围0–5，默认0.18。</param>
    /// <param name = "glyphTolerance">归一化像素膨胀半径，范围0–8，默认2。</param>
    /// <param name = "minimumConfidence">最低OCR置信度，范围0–1，默认0.75。</param>
    /// <param name = "barcodePrint">可选码印刷配置，具体项目启用以ROI的Tasks为准。</param>
    /// <param name = "barcodeType">明确码族，Auto仅在具有可靠结构时推断。</param>
    public FieldSettings(
        string? libraryId = null,
        int? libraryRevision = null,
        string? expected = null,
        string? pattern = null,
        string? allowedCharacters = null,
        int minimumLength = 0,
        int maximumLength = 128,
        bool equalCells = false,
        double maximumDifference = .18,
        int glyphTolerance = 2,
        double minimumConfidence = .75,
        BarcodePrintOptions? barcodePrint = null,
        EBarcodeKind barcodeType = EBarcodeKind.Auto
    )
    {
        if (
            (libraryId == null) != (libraryRevision == null)
            || libraryRevision < 1
            || (libraryId != null && string.IsNullOrWhiteSpace(libraryId))
        )
        {
            throw new ArgumentException("Library id and positive revision must be paired.");
        }

        if (
            minimumLength < 0
            || maximumLength < minimumLength
            || maximumLength > 1024
            || expected?.Length > 1024
            || pattern?.Length > 512
            || allowedCharacters?.Length > 1024
        )
        {
            throw new ArgumentException("Invalid field limits.");
        }

        if (
            equalCells
            && (
                string.IsNullOrEmpty(expected)
                || expected!.Length > 128
                || expected.Any(c => !IsAlphanumeric(c))
            )
        )
        {
            throw new ArgumentException(
                "Equal cells require an explicit ASCII alphanumeric sequence (max 128)."
            );
        }

        if (
            double.IsNaN(maximumDifference)
            || maximumDifference < 0
            || maximumDifference > 5
            || glyphTolerance < 0
            || glyphTolerance > 8
            || double.IsNaN(minimumConfidence)
            || minimumConfidence < 0
            || minimumConfidence > 1
        )
        {
            throw new ArgumentException("Invalid appearance thresholds.");
        }

        if (pattern != null)
        {
            _ = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            );
        }

        if (!Enum.IsDefined(typeof(EBarcodeKind), barcodeType))
        {
            throw new ArgumentOutOfRangeException(nameof(barcodeType));
        }

        BarcodeType = barcodeType;
        BarcodePrint = barcodePrint ?? new BarcodePrintOptions();
        LibraryId = libraryId;
        LibraryRevision = libraryRevision;
        Expected = expected;
        Pattern = pattern;
        AllowedCharacters = allowedCharacters;
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        EqualCells = equalCells;
        MaximumDifference = maximumDifference;
        GlyphTolerance = glyphTolerance;
        MinimumConfidence = minimumConfidence;
    }

    /// <summary>独立的条码印刷策略，仅用于条码ROI。</summary>
    public BarcodePrintOptions BarcodePrint { get; }

    /// <summary>要求的码族，即使解码失败仍然有效。</summary>
    public EBarcodeKind BarcodeType { get; }

    /// <summary>固定的类别标识。</summary>
    public string? LibraryId { get; }

    /// <summary>固定的不可变版本号。</summary>
    public int? LibraryRevision { get; }

    /// <summary>调用方提供的预期内容。</summary>
    public string? Expected { get; }

    /// <summary>具有执行时间限制的全匹配正则表达式。</summary>
    public string? Pattern { get; }

    /// <summary>可选的允许字符集合。</summary>
    public string? AllowedCharacters { get; }

    /// <summary>最小文本长度。</summary>
    public int MinimumLength { get; }

    /// <summary>最大文本长度。</summary>
    public int MaximumLength { get; }

    /// <summary>明确声明的等宽单元几何，不从OCR推断。</summary>
    public bool EqualCells { get; }

    /// <summary>归一化缺墨与多墨之和除以参考墨迹面积。</summary>
    public double MaximumDifference { get; }

    /// <summary>归一化像素容差，不是原图像素。</summary>
    public int GlyphTolerance { get; }

    /// <summary>外观候选要求的最低OCR置信度。</summary>
    public double MinimumConfidence { get; }

    /// <summary>支持的独立参考标签。</summary>
    /// <param name = "c">待判断是否为ASCII字母或数字的字符。</param>
    public static bool IsAlphanumeric(char c)
    {
        return c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';
    }
}
