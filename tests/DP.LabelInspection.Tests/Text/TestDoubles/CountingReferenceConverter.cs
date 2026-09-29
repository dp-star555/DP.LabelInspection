using System;
using System.Threading;
using DP.LabelInspection.Adapter.Vision;
using DP.LabelInspection.Contracts;
using DP.Vision;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 真实参考图转换探针：仍调用真实整帧复制，只额外记录转换次数与未归还的租约数。
/// <para>
/// 转换次数不随检测次数或 ROI 数增长，是“固定版本只转换一次”的可观测信号；
/// 租约余额归零表示缓存与借用方都没有继续占用参考图像素。
/// </para>
/// </summary>
internal sealed class CountingReferenceConverter : IGlyphReferenceImageConverter
{
    private int _conversions;
    private int _live;

    /// <summary>像素转换次数；同一固定版本应当只增长参考图个数那么多。</summary>
    internal int Conversions => Volatile.Read(ref _conversions);

    /// <summary>尚未归还的参考图租约句柄数。</summary>
    internal int LiveLeases => Volatile.Read(ref _live);

    /// <inheritdoc/>
    public IImageSource Convert(PixelSnapshot image)
    {
        Interlocked.Increment(ref _conversions);
        return new Tracked(Bridge.ToVision(image), this);
    }

    private void Retained() => Interlocked.Increment(ref _live);

    private void Released() => Interlocked.Decrement(ref _live);

    /// <summary>委托给真实图像源，只记录句柄的取得与归还。</summary>
    private sealed class Tracked : IImageSource
    {
        private readonly IImageSource _inner;
        private readonly CountingReferenceConverter _owner;
        private int _disposed;

        internal Tracked(IImageSource inner, CountingReferenceConverter owner)
        {
            _inner = inner;
            _owner = owner;
            _owner.Retained();
        }

        public ImageInfo Info => _inner.Info;

        public IImageSource Retain() => new Tracked(_inner.Retain(), _owner);

        public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize) =>
            _inner.ReadTile(level, tileX, tileY, tileSize);

        public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count) =>
            _inner.CopyTo(sourceOffset, destination, destinationOffset, count);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Released();
            _inner.Dispose();
        }
    }
}
