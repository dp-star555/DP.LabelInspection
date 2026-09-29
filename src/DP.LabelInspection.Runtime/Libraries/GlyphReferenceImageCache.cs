using System;
using System.Collections.Generic;
using System.Threading;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.Vision;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 按“库标识+版本”缓存已转换的 Vision 参考图，容量默认为 8，与固定版本字库缓存保持同样的窗口。
/// <para>
/// 固定版本不可变，因此同一版本最多转换一次像素。缓存自己为每个参考图持有一份租约，
/// 取用时返回 <c>Retain</c> 出来的独立租约；逐出或释放只归还缓存那一份，
/// 已经取出的租约继续可读，因此逐出不会让正在运行的比较读到已释放图像。
/// </para>
/// <para>
/// 线程安全：同一版本的并发取用只会转换一次，转换在缓存锁内完成，不会产生被丢弃的第二份租约。
/// </para>
/// </summary>
public sealed class GlyphReferenceImageCache : IDisposable
{
    /// <summary>默认容量：与固定版本字库缓存一致的最近使用窗口。</summary>
    public const int DefaultCapacity = 8;

    private readonly int _capacity;
    private readonly IGlyphReferenceImageConverter _converter;
    private readonly object _gate = new object();
    private readonly Dictionary<(string Id, int Revision), Entry> _index =
        new Dictionary<(string, int), Entry>();
    private long _stamp;
    private bool _disposed;

    /// <summary>创建缓存；转换器由调用方拥有，本类不释放它。</summary>
    /// <param name = "converter">参考图快照到 Vision 租约的转换实现。</param>
    /// <param name = "capacity">最多同时驻留的库版本数。</param>
    public GlyphReferenceImageCache(
        IGlyphReferenceImageConverter converter,
        int capacity = DefaultCapacity
    )
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>当前驻留的库版本数，用于确认逐出确实发生。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>取得某固定版本参考图的独立租约；该版本首次取用时才转换像素。</summary>
    /// <param name = "library">固定版本字库快照，提供已解码的参考图像素。</param>
    /// <returns>持有独立租约的参考模板集合，由调用方Dispose。</returns>
    public GlyphReferenceLease Acquire(GlyphLibrarySnapshot library)
    {
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GlyphReferenceImageCache));
            }

            var key = (library.Id, library.Revision);
            if (!_index.TryGetValue(key, out var entry))
            {
                // 转换放在锁内：并发取用同一版本时只会转换一次，也不会留下被丢弃的重复租约。
                entry = new Entry(_converter, library);
                _index.Add(key, entry);
                entry.Stamp = ++_stamp;
                Evict();
            }
            else
            {
                entry.Stamp = ++_stamp;
            }

            return entry.Lease();
        }
    }

    /// <summary>释放缓存持有的全部租约；已经取出的独立租约仍然有效。</summary>
    public void Dispose()
    {
        List<Entry> doomed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            doomed = new List<Entry>(_index.Values);
            _index.Clear();
        }

        foreach (var entry in doomed)
        {
            entry.Release();
        }
    }

    /// <summary>逐出最久未使用的版本；只归还缓存自己那份租约。</summary>
    private void Evict()
    {
        while (_index.Count > _capacity)
        {
            Entry? victim = null;
            (string Id, int Revision) victimKey = (string.Empty, 0);
            foreach (var pair in _index)
            {
                if (victim == null || pair.Value.Stamp < victim.Stamp)
                {
                    victim = pair.Value;
                    victimKey = pair.Key;
                }
            }

            if (victim == null)
            {
                return;
            }

            _index.Remove(victimKey);
            victim.Release();
        }
    }

    /// <summary>一个固定版本的已转换参考图；缓存持有基础租约，取用时再Retain出独立句柄。</summary>
    private sealed class Entry
    {
        private readonly string[] _characters;
        private readonly IImageSource[] _images;
        private readonly A.EGlyphBinarization[] _binarizations;
        private IImageSource[]? _owned;

        public Entry(IGlyphReferenceImageConverter converter, GlyphLibrarySnapshot library)
        {
            var characters = new List<string>();
            var images = new List<IImageSource>();
            var binarizations = new List<A.EGlyphBinarization>();
            try
            {
                foreach (var pair in library.Glyphs)
                {
                    images.Add(converter.Convert(pair.Value.Image));
                    characters.Add(pair.Key);
                    binarizations.Add(
                        AlgorithmContractAdapter.ToVisionBinarization(pair.Value.Binarization)
                    );
                }
            }
            catch
            {
                // 转换中途失败：已经取得的租约必须就地释放，不能挂进缓存。
                foreach (var image in images)
                {
                    image.Dispose();
                }

                throw;
            }

            _characters = characters.ToArray();
            _images = images.ToArray();
            _binarizations = binarizations.ToArray();
            _owned = _images;
        }

        /// <summary>最近使用序号，用于逐出最久未使用的版本。</summary>
        public long Stamp { get; set; }

        /// <summary>为本次取用创建独立租约；已逐出的版本不再可取用。</summary>
        public GlyphReferenceLease Lease()
        {
            if (_owned == null)
            {
                throw new ObjectDisposedException(nameof(GlyphReferenceImageCache));
            }

            var templates = new Dictionary<string, A.GlyphTemplate>(
                _characters.Length,
                StringComparer.Ordinal
            );
            var retained = new IImageSource[_images.Length];
            var taken = 0;
            try
            {
                for (; taken < _images.Length; taken++)
                {
                    retained[taken] = _images[taken].Retain();
                    templates.Add(
                        _characters[taken],
                        new A.GlyphTemplate(retained[taken], _binarizations[taken])
                    );
                }
            }
            catch
            {
                for (int i = 0; i < taken; i++)
                {
                    retained[i].Dispose();
                }

                throw;
            }

            return new GlyphReferenceLease(templates, retained);
        }

        /// <summary>归还缓存自己那份租约；已取出的独立租约不受影响。</summary>
        public void Release()
        {
            var owned = Interlocked.Exchange(ref _owned, null);
            if (owned == null)
            {
                return;
            }

            foreach (var image in owned)
            {
                image.Dispose();
            }
        }
    }
}
