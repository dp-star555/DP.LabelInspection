namespace DP.LabelInspection.Contracts;

/// <summary>中文与标点参考库的数量和像素预算；支持范围不是必须收齐的字符表。</summary>
public static class GlyphLibraryLimits
{
    /// <summary>每个字库或跨图暂存清单最多4096个不同Unicode单字参考。</summary>
    public const int MaximumReferences = 4096;

    /// <summary>每份字库或暂存清单最多1600万参考像素；BGR可能占用三倍字节，不是进程内存硬预算。</summary>
    public const long MaximumPixels = 16000000;
}
