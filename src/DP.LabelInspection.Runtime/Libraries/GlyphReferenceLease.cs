using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 固定版本字库参考图的独立租约。借用期间底层像素不会被缓存逐出释放；
/// 模板对象只借用图像，释放责任在本租约，因此算法不能替缓存管理生命周期。
/// </summary>
public sealed class GlyphReferenceLease : IDisposable
{
    private IImageSource[]? _retained;

    internal GlyphReferenceLease(
        IReadOnlyDictionary<string, A.GlyphTemplate> templates,
        IImageSource[] retained
    )
    {
        Templates = templates;
        _retained = retained;
    }

    /// <summary>按字符查询的参考模板；模板借用图像，不负责释放。</summary>
    public IReadOnlyDictionary<string, A.GlyphTemplate> Templates { get; }

    /// <summary>释放本次取用持有的租约；重复释放无副作用。</summary>
    public void Dispose()
    {
        var retained = Interlocked.Exchange(ref _retained, null);
        if (retained == null)
        {
            return;
        }

        foreach (var image in retained)
        {
            image.Dispose();
        }
    }
}
