using System;
using System.Linq;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;
using DP.Vision;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 字库参考图版本级缓存的验收：固定版本只转换一次、逐出不影响已借出的租约、最终租约归零。
/// 全部通过真实整帧复制的转换探针观察，不替换生产实现。
/// </summary>
[TestClass]
public sealed class GlyphReferenceImageCacheTests
{
    /// <summary>同一版本反复取用只转换一次，缓存自己那份租约在释放缓存时归零。</summary>
    [TestMethod]
    public void RepeatedAcquireOfSameVersionConvertsOnce()
    {
        var converter = new CountingReferenceConverter();
        var cache = new GlyphReferenceImageCache(converter);
        var library = Library("lib", 1, ("A", 10), ("B", 20));

        for (int i = 0; i < 5; i++)
        {
            using var lease = cache.Acquire(library);
            Assert.AreEqual(2, lease.Templates.Count);
            Assert.AreEqual(10, Read(lease.Templates["A"].Image)[0]);
        }

        Assert.AreEqual(2, converter.Conversions, "两个参考图各转换一次，与取用次数无关");
        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(2, converter.LiveLeases, "缓存自己持有每个参考图一份租约");

        cache.Dispose();
        Assert.AreEqual(0, converter.LiveLeases, "缓存释放后参考图租约归零");
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.Acquire(library));
    }

    /// <summary>不同版本各转换一次；容量内重复取用同一版本不会重新转换。</summary>
    [TestMethod]
    public void DistinctVersionsConvertOnceEachWithinCapacity()
    {
        var converter = new CountingReferenceConverter();
        var cache = new GlyphReferenceImageCache(converter, capacity: 8);

        for (int revision = 1; revision <= 8; revision++)
        {
            using var lease = cache.Acquire(Library("lib", revision, ("A", (byte)revision)));
            Assert.AreEqual(revision, Read(lease.Templates["A"].Image)[0]);
        }

        using (var again = cache.Acquire(Library("lib", 1, ("A", 1))))
        {
            Assert.AreEqual(1, again.Templates.Count);
        }

        Assert.AreEqual(8, converter.Conversions, "容量内的八个版本各转换一次");
        Assert.AreEqual(8, cache.Count);

        cache.Dispose();
        Assert.AreEqual(0, converter.LiveLeases);
    }

    /// <summary>
    /// 逐出只归还缓存自己那一份租约：被逐出版本的借出方仍能读到原像素，不会读到已释放图像。
    /// </summary>
    [TestMethod]
    public void EvictionKeepsBorrowedLeaseReadable()
    {
        var converter = new CountingReferenceConverter();
        var cache = new GlyphReferenceImageCache(converter, capacity: 1);

        // 借出不归还，模拟一次正在进行的比较。
        var borrowed = cache.Acquire(Library("lib", 1, ("A", 7)));
        using var later = cache.Acquire(Library("lib", 2, ("A", 9)));

        Assert.AreEqual(2, converter.Conversions);
        Assert.AreEqual(1, cache.Count);

        var pixels = Read(borrowed.Templates["A"].Image);
        Assert.AreEqual(16, pixels.Length);
        Assert.AreEqual(7, pixels[0], "被逐出后借出的参考图仍读到自己的像素");
        Assert.IsTrue(pixels.All(b => b == 7));

        Assert.AreEqual(
            3,
            converter.LiveLeases,
            "逐出恰好归还被逐出版本的一份缓存租约，借出的两份与新版缓存租约仍在"
        );

        borrowed.Dispose();
        later.Dispose();
        Assert.AreEqual(1, converter.LiveLeases, "借出方全部归还后只剩缓存那一份");

        cache.Dispose();
        Assert.AreEqual(0, converter.LiveLeases);
    }

    /// <summary>同一版本被并发取用时只转换一次，不会留下被丢弃的重复租约。</summary>
    [TestMethod]
    public void ConcurrentAcquireOfSameVersionConvertsOnce()
    {
        var converter = new CountingReferenceConverter();
        var cache = new GlyphReferenceImageCache(converter);
        var library = Library("lib", 1, ("A", 1), ("B", 2), ("C", 3));
        var leases = new GlyphReferenceLease[64];

        try
        {
            Parallel.For(0, leases.Length, i => leases[i] = cache.Acquire(library));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease?.Dispose();
            }
        }

        Assert.AreEqual(3, converter.Conversions, "三个参考图各转换一次");
        Assert.AreEqual(3, converter.LiveLeases);

        cache.Dispose();
        Assert.AreEqual(0, converter.LiveLeases);
    }

    private static GlyphLibrarySnapshot Library(
        string id,
        int revision,
        params (string Character, byte Value)[] glyphs
    )
    {
        return new GlyphLibrarySnapshot(
            id,
            revision,
            id,
            glyphs.Select(g => new GlyphReference(g.Character, Glyph(g.Value), "sha-" + g.Value, "fixed"))
        );
    }

    private static PixelSnapshot Glyph(byte value)
    {
        var pixels = new byte[16];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = value;
        }

        return new PixelSnapshot(4, 4, EImagePixelFormat.Gray8, pixels);
    }

    private static byte[] Read(IImageSource image)
    {
        var buffer = new byte[image.Info.ByteLength];
        image.CopyTo(0, buffer, 0, buffer.Length);
        return buffer;
    }
}
