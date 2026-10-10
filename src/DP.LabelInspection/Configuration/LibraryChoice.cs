using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernPropertyGrid.WinForms;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>ROI参数中的库绑定选项：库标识＋固定修订；<see cref="Id"/>为null表示不绑定。</summary>
[TypeConverter(typeof(LibraryChoiceConverter))]
internal sealed class LibraryChoice : IEquatable<LibraryChoice>
{
    internal static readonly LibraryChoice None = new LibraryChoice(null, null, null, false);

    internal LibraryChoice(string? id, int? revision, string? name, bool latest)
    {
        Id = string.IsNullOrWhiteSpace(id) ? null : id;
        Revision = Id == null ? null : revision;
        Name = name;
        Latest = latest;
    }

    internal string? Id { get; }

    internal int? Revision { get; }

    internal string? Name { get; }

    internal bool Latest { get; }

    public bool Equals(LibraryChoice? other) =>
        other != null && string.Equals(Id, other.Id, StringComparison.Ordinal) && Revision == other.Revision;

    public override bool Equals(object? obj) => Equals(obj as LibraryChoice);

    public override int GetHashCode() => (Id ?? "").GetHashCode() ^ (Revision ?? 0);

    public override string ToString() =>
        Id == null ? "（不绑定）"
        : (Name ?? Id) + " · r" + (Revision?.ToString(CultureInfo.InvariantCulture) ?? "?")
            + (Latest ? "（最新）" : Name == null ? "（库不可用）" : "");
}

/// <summary>可绑定的字库与异常模型库；由规则视图从宿主管理器读取后交给各ROI。</summary>
internal sealed class LibrarySources
{
    internal static readonly LibrarySources Empty = new LibrarySources(null, null);

    private readonly IReadOnlyList<GlyphLibraryInfo> _glyphs;
    private readonly IReadOnlyList<AnomalyLibraryInfo> _anomalies;

    internal LibrarySources(IGlyphLibraryManager? glyphs, IAnomalyLibraryManager? anomalies)
    {
        _glyphs = glyphs?.ListLibraries() ?? Array.Empty<GlyphLibraryInfo>();
        _anomalies = anomalies?.ListAnomalyLibraries() ?? Array.Empty<AnomalyLibraryInfo>();
    }

    internal LibraryChoice Glyph(string? id, int? revision) =>
        Choice(id, revision, _glyphs.FirstOrDefault(l => l.Id == id) is { } l ? (l.Name, l.Revision) : null);

    internal LibraryChoice Anomaly(string? id, int? revision) =>
        Choice(id, revision, _anomalies.FirstOrDefault(l => l.Id == id) is { } l ? (l.Name, l.Revision) : null);

    // 下拉项：不绑定、每个库的最新修订，以及当前已绑定的旧修订（保持显示，不自动升级）。
    internal IReadOnlyList<LibraryChoice> GlyphChoices(LibraryChoice current) =>
        Choices(current, _glyphs.Select(l => new LibraryChoice(l.Id, l.Revision, l.Name, true)));

    internal IReadOnlyList<LibraryChoice> AnomalyChoices(LibraryChoice current) =>
        Choices(current, _anomalies.Select(l => new LibraryChoice(l.Id, l.Revision, l.Name, true)));

    private static LibraryChoice Choice(string? id, int? revision, (string Name, int Latest)? head) =>
        new LibraryChoice(id, revision, head?.Name, head != null && head.Value.Latest == revision);

    private static IReadOnlyList<LibraryChoice> Choices(LibraryChoice current, IEnumerable<LibraryChoice> latest)
    {
        var result = new List<LibraryChoice> { LibraryChoice.None };
        result.AddRange(latest);
        if (!result.Contains(current))
        {
            result.Insert(1, current);
        }

        return result;
    }
}

/// <summary>显示为“库名 · r修订”；原生属性表从所属ROI读取可选项。</summary>
internal sealed class LibraryChoiceConverter : TypeConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
        new StandardValuesCollection(
            context?.Instance is RegionEditor.EditableRegion region && context.PropertyDescriptor != null
                ? region.Choices(context.PropertyDescriptor.Name).ToArray()
                : new[] { LibraryChoice.None }
        );

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) && context?.Instance is RegionEditor.EditableRegion;

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text && context?.Instance is RegionEditor.EditableRegion region && context.PropertyDescriptor != null
            ? region.Choices(context.PropertyDescriptor.Name).FirstOrDefault(c => c.ToString() == text) ?? LibraryChoice.None
            : base.ConvertFrom(context, culture, value);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is LibraryChoice choice
            ? choice.ToString()
            : base.ConvertTo(context, culture, value, destinationType);
}

/// <summary>现代属性表中把库绑定显示为下拉框；可选项来自所属ROI当前的库列表。</summary>
internal sealed class LibraryChoiceEditorProvider : IPropertyEditorProvider
{
    internal static readonly LibraryChoiceEditorProvider Instance = new LibraryChoiceEditorProvider();

    public int Priority => 100;

    public bool CanEdit(PropertyDescriptor property) => property.PropertyType == typeof(LibraryChoice);

    public Control CreateEditor(PropertyEditorContext context)
    {
        var region = (RegionEditor.EditableRegion)context.Owner;
        var select = new ModernSelect { Theme = ModernTheme.Dark, DropDownAnimationDuration = 0 };
        foreach (var choice in region.Choices(context.Property.Name))
        {
            select.Items.Add(choice);
        }

        select.SelectedItem = context.Value as LibraryChoice ?? LibraryChoice.None;
        select.SelectedIndexChanged += (_, _) =>
        {
            if (select.SelectedItem is LibraryChoice choice && !choice.Equals(context.Property.GetValue(region)))
            {
                context.CommitValue(choice);
            }
        };
        return select;
    }
}
