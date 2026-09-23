using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>固定配方，在后台执行前校验图像尺寸及区域重叠。</summary>
public sealed class InspectionRecipe
{
    /// <summary>创建可复用且绑定图像尺寸的配方。</summary>
    /// <param name = "name">显示名称。</param>
    /// <param name = "width">预期图像宽度。</param>
    /// <param name = "height">预期图像高度。</param>
    /// <param name = "mode">参考模式。</param>
    /// <param name = "alignment">配准策略。</param>
    /// <param name = "regions">复制到只读集合的区域定义。</param>
    /// <param name = "options">经过校验的设置，null使用默认值。</param>
    /// <param name = "bindings">可选原始观测约束，复制后按命名文字/条码ROI校验。</param>
    public InspectionRecipe(
        string name,
        int width,
        int height,
        EInspectionMode mode,
        EAlignmentMode alignment,
        IEnumerable<InspectionRegion> regions,
        InspectionOptions? options = null,
        IEnumerable<FieldBinding>? bindings = null
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Recipe name required.", nameof(name));
        }

        if (width < 1 || height < 1 || width > 12000 || height > 12000 || (long)width * height > 16000000)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (
            !Enum.IsDefined(typeof(EInspectionMode), mode)
            || !Enum.IsDefined(typeof(EAlignmentMode), alignment)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (regions == null)
        {
            throw new ArgumentNullException(nameof(regions));
        }

        var list = regions.ToArray();
        if (
            list.Length > 128
            || list.Any(r => r == null)
            || list.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != list.Length
        )
        {
            throw new ArgumentException(
                "Regions must have unique names and number at most 128.",
                nameof(regions)
            );
        }

        Name = name;
        Width = width;
        Height = height;
        Mode = mode;
        Alignment = alignment;
        Regions = new ReadOnlyCollection<InspectionRegion>(list);
        Options = options ?? new InspectionOptions();
        var rules = (bindings ?? Array.Empty<FieldBinding>()).ToArray();
        bool Readable(string key)
        {
            return list.Any(r =>
                r.Name == key && (r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode)
            );
        }

        if (
            rules.Length > 256
            || rules.Any(b =>
                b == null || !Readable(b.Target) || (b.Source == EBindingSource.Region && !Readable(b.Key))
            )
        )
        {
            throw new ArgumentException("Bindings must refer to text/barcode ROIs.");
        }

        Bindings = Array.AsReadOnly(rules);
    }

    /// <summary>配方名称。</summary>
    public string Name { get; }

    /// <summary>预期宽度。</summary>
    public int Width { get; }

    /// <summary>预期高度。</summary>
    public int Height { get; }

    /// <summary>参考策略。</summary>
    public EInspectionMode Mode { get; }

    /// <summary>配准策略。</summary>
    public EAlignmentMode Alignment { get; }

    /// <summary>不可变区域列表。</summary>
    public IReadOnlyList<InspectionRegion> Regions { get; }

    /// <summary>检测设置。</summary>
    public InspectionOptions Options { get; }

    /// <summary>原始观测的内容约束；分阶段调度会识别依赖环并阻断，不能通过相互替换生成已确认读数。</summary>
    public IReadOnlyList<FieldBinding> Bindings { get; }
}
