using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using V = DP.Vision;

namespace DP.LabelInspection.Contracts;

/// <summary>与UI无关的检测输入，不要求文件路径或原生图像对象。</summary>
public sealed class InspectionRequest : IDisposable
{
    private readonly ImageFrame? _actualSnapshot;
    private readonly ImageFrame? _referenceSnapshot;
    private readonly V.ImageFrame? _visionActual;
    private readonly V.ImageFrame? _visionReference;
    private readonly Lazy<ImageFrame>? _convertedActual;
    private readonly Lazy<ImageFrame>? _convertedReference;
    private bool _disposed;

    /// <summary>创建不可变请求。</summary>
    /// <param name = "actual">独立图像快照。</param>
    /// <param name = "recipe">固定配方。</param>
    /// <param name = "reference">可选参考快照。</param>
    /// <param name = "cycleId">宿主采集周期标识，用于匹配业务数据。</param>
    /// <param name = "taskData">可选的本周期不可变数据。</param>
    /// <param name = "frameId">宿主提供的当前帧身份；不提供时本请求生成独立身份。</param>
    public InspectionRequest(
        ImageFrame actual,
        InspectionRecipe recipe,
        ImageFrame? reference = null,
        string? cycleId = null,
        TaskDataSnapshot? taskData = null,
        string? frameId = null
    )
    {
        _actualSnapshot = actual ?? throw new ArgumentNullException(nameof(actual));
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (frameId != null && string.IsNullOrWhiteSpace(frameId))
        {
            throw new ArgumentException("Invalid frame identity.", nameof(frameId));
        }

        FrameId = frameId ?? Guid.NewGuid().ToString("N");
        if (cycleId != null && (string.IsNullOrWhiteSpace(cycleId) || cycleId.Length > 200))
        {
            throw new ArgumentException("Invalid capture-cycle identity.", nameof(cycleId));
        }

        _referenceSnapshot = reference;
        CycleId = cycleId;
        TaskData = taskData;
    }

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
        _convertedActual = new Lazy<ImageFrame>(() => ToSnapshot(_visionActual!.Image));
        if (_visionReference != null)
            _convertedReference = new Lazy<ImageFrame>(() => ToSnapshot(_visionReference.Image));
    }

    /// <summary>以Vision原图租约创建请求；独立Retain输入，请在检测及保存结束后释放请求。旧算法首次访问Actual时才复制像素。</summary>
    /// <param name="actual">实际原图及帧身份。</param>
    /// <param name="recipe">固定配方。</param>
    /// <param name="reference">可选整图参考。</param>
    /// <param name="cycleId">本周期业务身份。</param>
    /// <param name="taskData">本周期引导数据。</param>
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

    private static ImageFrame ToSnapshot(V.IImageSource image)
    {
        var info = image.Info;
        var pixels = new byte[info.ByteLength];
        image.CopyTo(0, pixels, 0, pixels.Length);
        return new ImageFrame(
            info.Width,
            info.Height,
            info.Layout == V.EPixelLayout.Gray8 ? EImagePixelFormat.Gray8 : EImagePixelFormat.Bgr24,
            pixels
        );
    }

    /// <summary>当前原图尺寸；读取Vision请求时不触发旧快照复制。</summary>
    public int ImageWidth
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual?.Image.Info.Width ?? Actual.Width;
        }
    }

    /// <summary>当前原图高度；读取Vision请求时不触发旧快照复制。</summary>
    public int ImageHeight
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual?.Image.Info.Height ?? Actual.Height;
        }
    }

    /// <summary>当前图像的身份；不同于业务采集周期，定位证据不可跨帧复用。</summary>
    public string FrameId { get; }

    /// <summary>宿主独立提供的采集周期。</summary>
    public string? CycleId { get; }

    /// <summary>固定的外部预期值，不从OCR推断。</summary>
    public TaskDataSnapshot? TaskData { get; }

    /// <summary>是否提供整图参考；Vision请求不需先转换快照。</summary>
    public bool HasReference => _visionReference != null || _referenceSnapshot != null;

    /// <summary>参考图宽度，未提供时为空。</summary>
    public int? ReferenceWidth => _visionReference?.Image.Info.Width ?? _referenceSnapshot?.Width;

    /// <summary>参考图高度，未提供时为空。</summary>
    public int? ReferenceHeight => _visionReference?.Image.Info.Height ?? _referenceSnapshot?.Height;

    /// <summary>供现有算法/存储使用的独立像素快照；Vision请求首次访问时才复制。</summary>
    public ImageFrame Actual
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _actualSnapshot ?? _convertedActual!.Value;
        }
    }

    /// <summary>配方快照。</summary>
    public InspectionRecipe Recipe { get; }

    /// <summary>供现有算法/存储使用的可选整图参考快照。</summary>
    public ImageFrame? Reference
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _referenceSnapshot ?? _convertedReference?.Value;
        }
    }

    /// <summary>Vision请求持有的原图租约；仅借用，不得单独释放。旧请求为null。</summary>
    public V.IImageSource? VisionSource
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionActual?.Image;
        }
    }

    /// <summary>Vision请求持有的参考图租约；仅借用，不得单独释放。旧请求为null。</summary>
    public V.IImageSource? VisionReference
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InspectionRequest));
            return _visionReference?.Image;
        }
    }

    /// <summary>在后台任务和报告保存结束后释放请求持有的Vision租约；旧快照请求无需释放。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _visionActual?.Dispose();
        }
        finally
        {
            _visionReference?.Dispose();
        }
    }
}
