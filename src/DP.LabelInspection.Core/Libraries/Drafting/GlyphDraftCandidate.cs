using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>当前图像中的不可变候选，包含无标签手动裁图，不是已批准参考。</summary>
public sealed class GlyphDraftCandidate
{
    internal GlyphDraftCandidate(
        string id,
        string label,
        PixelRect bounds,
        ImageFrame image,
        string operation,
        string provisionalCharacter = ""
    )
    {
        Id = id;
        Label = label;
        Bounds = bounds;
        Image = image;
        Operation = operation;
        ProvisionalCharacter = provisionalCharacter;
    }

    /// <summary>当前图像内稳定的候选标识。</summary>
    public string Id { get; }

    /// <summary>可编辑的独立标签，人工复核前可以为空。</summary>
    public string Label { get; }

    /// <summary>原图坐标。</summary>
    public PixelRect Bounds { get; }

    /// <summary>独立的不可变像素，不是指向下一张图的活动视图。</summary>
    public ImageFrame Image { get; }

    /// <summary>供人工复核和溯源使用的来源或编辑操作。</summary>
    public string Operation { get; }

    /// <summary>原始待确认字符，与人工标签编辑分开保存；纯手动裁图为空。</summary>
    public string ProvisionalCharacter { get; }
}
