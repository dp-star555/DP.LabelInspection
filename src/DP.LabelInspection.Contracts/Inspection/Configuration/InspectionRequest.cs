using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using V = DP.Vision;

namespace DP.LabelInspection.Contracts;

/// <summary>与UI无关的检测输入，不要求文件路径或原生图像对象。运行时像素只来自Vision原图租约。</summary>
public sealed class InspectionRequest : IDisposable
{
    private readonly V.ImageFrame _visionActual;
    private readonly V.ImageFrame? _visionReference;
    private bool _disposed;

    private InspectionRequest(
        V.ImageFrame actual,
        InspectionRecipe recipe,
        V.ImageFrame? reference,
        string? cycleId,
        TaskDataSnapshot? taskData
    )
    {
        if (actual == null)
            throw new ArgumentNullException(nameof(actual));
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (cycleId != null && (string.IsNullOrWhiteSpace(cycleId) || cycleId.Length > 200))
            throw new ArgumentException("Invalid capture-cycle identity.", nameof(cycleId));
        CheckLayout(actual.Image);
        if (reference != null)
            CheckLayout(reference.Image);
        V.ImageFrame? retained = null;
        try
        {
            retained = actual.Retain();
            _visionReference = reference?.Retain();
            _visionActual = retained;
        }
        catch
        {
            retained?.Dispose();
            throw;
        }
        FrameId = actual.FrameId;
        CycleId = cycleId;
        TaskData = taskData;
    }

    /// <summary>以Vision原图租约创建请求；独立Retain输入，请在检测及保存结束后释放请求。</summary>
    /// <param name = "actual">实际原图及帧身份。</param>
    /// <param name = "recipe">固定配方。</param>
    /// <param name = "reference">可选整图参考。</param>
    /// <param name = "cycleId">本周期业务身份。</param>
    /// <param name = "taskData">本周期引导数据。</param>
    /// <returns>拥有独立Vision租约的检测请求。</returns>
    public static InspectionRequest FromVision(
        V.ImageFrame actual,
        InspectionRecipe recipe,
        V.ImageFrame? reference = null,
        string? cycleId = null,
        TaskDataSnapshot? taskData = null
    ) => new InspectionRequest(actual, recipe, reference, cycleId, taskData);

    private static void CheckLayout(V.IImageSource image)
    {
        var info = image.Info;
        if (
            info.Layout != V.EPixelLayout.Gray8 && info.Layout != V.EPixelLayout.Bgr24
            || info.Width > 12000
            || info.Height > 12000
            || (long)info.Width * info.Height > 16000000
        )
            throw new NotSupportedException("Label inspection requires Gray8/Bgr24 and at most 16M pixels.");
    }

    /// <summary>把租约复制为独立标签快照。只在调用方明确要求像素时执行，不做隐式缓存。</summary>
    private static PixelSnapshot ToSnapshot(V.IImageSource image)
    {
        var info = image.Info;
        var pixels = new byte[info.ByteLength];
        image.CopyTo(0, pixels, 0, pixels.Length);
        return new PixelSnapshot(
            info.Width,
            info.Height,
            info.Layout == V.EPixelLayout.Gray8 ? EImagePixelFormat.Gray8 : EImagePixelFormat.Bgr24,
            pixels
        );
    }

    /// <summary>当前原图尺寸。</summary>
    public int ImageWidth
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual.Image.Info.Width;
        }
    }

    /// <summary>当前原图高度。</summary>
    public int ImageHeight
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual.Image.Info.Height;
        }
    }

    /// <summary>当前图像的身份；不同于业务采集周期，定位证据不可跨帧复用。</summary>
    public string FrameId { get; }

    /// <summary>宿主独立提供的采集周期。</summary>
    public string? CycleId { get; }

    /// <summary>固定的外部预期值，不从OCR推断。</summary>
    public TaskDataSnapshot? TaskData { get; }

    /// <summary>是否提供整图参考。</summary>
    public bool HasReference => _visionReference != null;

    /// <summary>参考图宽度，未提供时为空。</summary>
    public int? ReferenceWidth => _visionReference?.Image.Info.Width;

    /// <summary>参考图高度，未提供时为空。</summary>
    public int? ReferenceHeight => _visionReference?.Image.Info.Height;

    /// <summary>配方快照。</summary>
    public InspectionRecipe Recipe { get; }

    /// <summary>
    /// 把本次请求的原图租约复制为独立标签快照，供持久化、显示等确实需要像素的接口使用。
    /// 每次调用都会复制整帧，因此不要在检测热路径上调用。
    /// </summary>
    /// <returns>调用方拥有的独立快照；不是本次检测的运行时输入。</returns>
    public PixelSnapshot CreateActualSnapshot()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InspectionRequest));
        return ToSnapshot(_visionActual.Image);
    }

    /// <summary>把参考图租约复制为独立标签快照；未提供参考时为空。</summary>
    /// <returns>调用方拥有的独立快照，或空。</returns>
    public PixelSnapshot? CreateReferenceSnapshot()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InspectionRequest));
        return _visionReference == null ? null : ToSnapshot(_visionReference.Image);
    }

    /// <summary>本次检测持有的原图租约；仅借用，不得单独释放。</summary>
    public V.IImageSource VisionSource
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual.Image;
        }
    }

    /// <summary>本次检测持有的参考图租约；仅借用，不得单独释放。未提供参考时为空。</summary>
    public V.IImageSource? VisionReference
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionReference?.Image;
        }
    }

    /// <summary>在后台任务和报告保存结束后释放请求持有的Vision租约。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _visionActual.Dispose();
        }
        finally
        {
            _visionReference?.Dispose();
        }
    }
}
