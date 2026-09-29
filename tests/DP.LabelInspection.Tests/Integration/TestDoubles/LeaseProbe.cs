using System;
using System.Threading;
using DP.Vision;

namespace DP.LabelInspection.Tests;

/// <summary>
/// 真实Vision租约探针：委托给真实图像源，只记录租约增减与复制形态。
/// 整帧签名（源偏移0、目标偏移0、长度等于整帧字节数）只由检测请求的惰性快照产生；
/// 逐行读取走CopyRegion，长度等于一行字节数，因此两者可区分。
/// </summary>
internal sealed class LeaseProbe : IImageSource
{
    private readonly IImageSource _inner;
    private readonly Counters _counters;
    private int _disposed;

    /// <summary>包装真实图像源，并把根句柄计入租约。</summary>
    internal LeaseProbe(IImageSource inner)
    {
        _inner = inner;
        _counters = new Counters();
        _counters.Add();
    }

    private LeaseProbe(IImageSource inner, Counters counters)
    {
        _inner = inner;
        _counters = counters;
        _counters.Add();
    }

    /// <summary>当前未归还的租约数；归零表示没有后台任务或证据继续占用。</summary>
    internal int LiveLeases => _counters.Live;

    /// <summary>逐行读取次数，用于确认运行期真的读到了像素。</summary>
    internal int RowCopies => _counters.RowCopies;

    /// <summary>整帧复制次数；非零表示检测请求物化了标签整帧快照。</summary>
    internal int WholeFrameCopies => _counters.WholeFrameCopies;

    public ImageInfo Info => _inner.Info;

    public IImageSource Retain() => new LeaseProbe(_inner.Retain(), _counters);

    public IImageSource ReadTile(int level, int x, int y, int size) => _inner.ReadTile(level, x, y, size);

    public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count)
    {
        if (sourceOffset == 0 && destinationOffset == 0 && count == _inner.Info.ByteLength)
        {
            _counters.CountWholeFrame();
        }
        else
        {
            _counters.CountRow();
        }

        _inner.CopyTo(sourceOffset, destination, destinationOffset, count);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _counters.Release();
        _inner.Dispose();
    }

    private sealed class Counters
    {
        private int _live;
        private int _rowCopies;
        private int _wholeFrameCopies;

        public int Live => Volatile.Read(ref _live);
        public int RowCopies => Volatile.Read(ref _rowCopies);
        public int WholeFrameCopies => Volatile.Read(ref _wholeFrameCopies);

        public void Add() => Interlocked.Increment(ref _live);
        public void Release() => Interlocked.Decrement(ref _live);
        public void CountRow() => Interlocked.Increment(ref _rowCopies);
        public void CountWholeFrame() => Interlocked.Increment(ref _wholeFrameCopies);
    }
}
