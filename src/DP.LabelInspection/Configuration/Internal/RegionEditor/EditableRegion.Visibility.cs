using System;
using System.ComponentModel;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

internal static partial class RegionEditor
{
    /// <summary>
    /// 属性面板按需显示：先按检查类型列出可用的检测项目（读取、A、B），只有开启的项目才显示其专属参数。
    /// 隐藏的参数保留原值并照常写入配置，重新开启后恢复显示。
    /// </summary>
    internal sealed partial class EditableRegion : ICustomTypeDescriptor
    {
        /// <summary>当前类型及检测项目下应显示的属性。</summary>
        /// <param name = "property">属性名称。</param>
        internal bool IsShown(string property)
        {
            bool text = Kind == ERegionKind.Text,
                barcode = Kind == ERegionKind.Barcode,
                inspected = Kind != ERegionKind.Ignore,
                data = (text || barcode) && ReadData,
                a = inspected && CheckQuality,
                b = inspected && DetectAnomaly,
                perCharacter = b && text && AnomalyPerCharacter,
                segmented = text && (data || a || perCharacter);
            switch (property)
            {
                case nameof(ReadData):
                    return text || barcode;
                case nameof(CheckQuality):
                case nameof(DetectAnomaly):
                    return inspected;
                case nameof(SingleLine):
                case nameof(EqualCells):
                case nameof(MinimumConfidence):
                    return segmented;
                case nameof(GlyphLibrary):
                case nameof(MaximumDifference):
                case nameof(GlyphTolerance):
                    return text && a;
                case nameof(Expected):
                    // 等宽单元用预期内容确定字数和身份，未读取数据时也需要。
                    return data || segmented && EqualCells;
                case nameof(Pattern):
                case nameof(AllowedCharacters):
                case nameof(MinimumLength):
                case nameof(MaximumLength):
                    return data;
                case nameof(BarcodeType):
                    return barcode && (data || a);
                case nameof(CheckQrQuietZone):
                    return barcode && a && BarcodeType != EBarcodeKind.OneDimensional;
                case nameof(DetectInkLoss):
                    return barcode && a && BarcodeType != EBarcodeKind.QrCode;
                case nameof(MinimumInkLoss):
                    return barcode && a && BarcodeType != EBarcodeKind.QrCode && DetectInkLoss;
                case nameof(BarcodeMinimumArea):
                case nameof(BarcodeMinimumFraction):
                case nameof(BarcodeEdgeTolerance):
                    return barcode && a;
                case nameof(AnomalyLibrary):
                case nameof(AnomalyModelKey):
                    return b;
                case nameof(AnomalyPerCharacter):
                    return b && text;
                case nameof(AnomalyInkLoss):
                    return perCharacter;
                default:
                    return true;
            }
        }

        /// <summary>当前显示的属性名称（按名称排序后以逗号连接），用于判断面板结构是否需要重建。</summary>
        internal string ShownSignature() =>
            string.Join(
                ",",
                TypeDescriptor
                    .GetProperties(this)
                    .Cast<PropertyDescriptor>()
                    .Where(p => p.IsBrowsable)
                    .Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
            );

        PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties() =>
            Shown(TypeDescriptor.GetProperties(this, true));

        PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties(Attribute[]? attributes) =>
            Shown(TypeDescriptor.GetProperties(this, attributes, true));

        private PropertyDescriptorCollection Shown(PropertyDescriptorCollection all) =>
            new PropertyDescriptorCollection(
                all.Cast<PropertyDescriptor>().Where(p => IsShown(p.Name)).ToArray(),
                true
            );

        AttributeCollection ICustomTypeDescriptor.GetAttributes() => TypeDescriptor.GetAttributes(this, true);

        string? ICustomTypeDescriptor.GetClassName() => TypeDescriptor.GetClassName(this, true);

        string? ICustomTypeDescriptor.GetComponentName() => TypeDescriptor.GetComponentName(this, true);

        TypeConverter ICustomTypeDescriptor.GetConverter() => TypeDescriptor.GetConverter(this, true);

        EventDescriptor? ICustomTypeDescriptor.GetDefaultEvent() =>
            TypeDescriptor.GetDefaultEvent(this, true);

        PropertyDescriptor? ICustomTypeDescriptor.GetDefaultProperty() =>
            TypeDescriptor.GetDefaultProperty(this, true);

        object? ICustomTypeDescriptor.GetEditor(Type editorBaseType) =>
            TypeDescriptor.GetEditor(this, editorBaseType, true);

        EventDescriptorCollection ICustomTypeDescriptor.GetEvents() => TypeDescriptor.GetEvents(this, true);

        EventDescriptorCollection ICustomTypeDescriptor.GetEvents(Attribute[]? attributes) =>
            TypeDescriptor.GetEvents(this, attributes, true);

        object ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor? pd) => this;
    }
}
