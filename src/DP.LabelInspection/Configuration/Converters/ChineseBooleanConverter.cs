using System;
using System.ComponentModel;
using System.Globalization;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

// 仅作UI显示转换；契约枚举、布尔值和序列化属性名保持不变。
internal sealed class ChineseBooleanConverter : BooleanConverter
{
    public ChineseBooleanConverter() { }

    /// <summary>将属性值转换为中文显示形式，保留底层契约值。</summary>
    /// <param name = "context">属性转换上下文，可为null。</param>
    /// <param name = "culture">格式转换使用的区域信息，可为null。</param>
    /// <param name = "value">待转为显示形式的值，可为null。</param>
    /// <param name = "destinationType">目标类型；字符串目标使用中文显示。</param>
    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType
    )
    {
        return destinationType == typeof(string) && value is bool flag
            ? flag
                ? "是"
                : "否"
            : base.ConvertTo(context, culture, value, destinationType);
    }

    /// <summary>将中文显示值解析回底层属性值。</summary>
    /// <param name = "context">属性转换上下文，可为null。</param>
    /// <param name = "culture">格式转换使用的区域信息，可为null。</param>
    /// <param name = "value">待解析的显示值，不改变持久化字段名称。</param>
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value is string text && text == "是" ? true
            : value is string no && no == "否" ? false
            : base.ConvertFrom(context, culture, value);
    }
}
