using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>固定版本的不可变类别，源排列顺序不影响查询。</summary>
public sealed class GlyphLibrarySnapshot
{
    /// <summary>将参考复制到序数比较字典。</summary>
    /// <param name = "id">字库类别标识。</param>
    /// <param name = "revision">正整数不可变版本号。</param>
    /// <param name = "name">类别显示名称。</param>
    /// <param name = "glyphs">独立参考集合，复制到大小写敏感字典。</param>
    public GlyphLibrarySnapshot(string id, int revision, string name, IEnumerable<GlyphReference> glyphs)
    {
        Id = id;
        Revision = revision;
        Name = name;
        if (glyphs == null)
        {
            throw new ArgumentNullException(nameof(glyphs));
        }

        var references = new Dictionary<string, GlyphReference>(StringComparer.Ordinal);
        long pixels = 0;
        foreach (var glyph in glyphs)
        {
            if (glyph == null || references.Count >= GlyphLibraryLimits.MaximumReferences)
            {
                throw new ArgumentException("Invalid or excessive glyph references.", nameof(glyphs));
            }

            pixels += (long)glyph.Image.Width * glyph.Image.Height;
            if (pixels > GlyphLibraryLimits.MaximumPixels)
            {
                throw new ArgumentException(
                    "Glyph library exceeds 16000000 reference pixels.",
                    nameof(glyphs)
                );
            }

            references.Add(glyph.Character, glyph);
        }

        Glyphs = new ReadOnlyDictionary<string, GlyphReference>(references);
    }

    /// <summary>类别标识。</summary>
    public string Id { get; }

    /// <summary>不可变版本号。</summary>
    public int Revision { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>独立参考查询表。</summary>
    public IReadOnlyDictionary<string, GlyphReference> Glyphs { get; }
}
