using System;
using System.Linq;
using DP.Vision;
using A = DP.Vision.Algorithms;
using L = DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>现有不可变标签快照与独立算法契约之间的转换；会复制像素，不宣称零复制。</summary>
public static class AlgorithmContractAdapter
{
    /// <summary>创建调用方拥有的只读算法租约。</summary>
    /// <param name = "frame">待复制的不可变标签图像快照。</param>
    public static IImageSource ToVision(L.ImageFrame frame)
    {
        return VisionImage.CopyFrom(
            new ImageInfo(
                frame.Width,
                frame.Height,
                frame.Format == L.EImagePixelFormat.Gray8 ? EPixelLayout.Gray8 : EPixelLayout.Bgr24
            ),
            frame.CopyPixels()
        );
    }

    /// <summary>将支持的像素布局复制到现有不可变报告模型。</summary>
    /// <param name = "frame">借用的通用图像租约，支持的像素会复制为标签快照。</param>
    public static L.ImageFrame ToLabel(IImageSource frame)
    {
        if (frame == null)
            throw new ArgumentNullException(nameof(frame));
        // 在复制大数组前检查业务快照的尺寸上限，通用源并不意味着标签引擎支持任意大图。
        if (
            frame.Info.Width > 12000
            || frame.Info.Height > 12000
            || (long)frame.Info.Width * frame.Info.Height > 16000000
        )
            throw new ArgumentOutOfRangeException(nameof(frame), "Label image dimensions exceed limits.");
        if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
        {
            throw new NotSupportedException("Legacy evidence requires Gray8/Bgr24.");
        }

        var bytes = new byte[frame.Info.ByteLength];
        frame.CopyTo(0, bytes, 0, bytes.Length);
        return new L.ImageFrame(
            frame.Info.Width,
            frame.Info.Height,
            frame.Info.Layout == EPixelLayout.Gray8 ? L.EImagePixelFormat.Gray8 : L.EImagePixelFormat.Bgr24,
            bytes
        );
    }

    /// <summary>保留离散像素坐标。</summary>
    /// <param name = "r">标签侧原图整数矩形。</param>
    public static A.PixelBounds ToVision(L.PixelRect r)
    {
        return new A.PixelBounds(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>保留离散像素坐标。</summary>
    /// <param name = "r">中立的原图整数像素范围。</param>
    public static L.PixelRect ToLabel(A.PixelBounds r)
    {
        return new L.PixelRect(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>在释放中立图像租约之前复制物理证据。</summary>
    /// <param name = "s">借用的中立物理分割结果；释放前复制其字符图块。</param>
    public static L.CharacterSegmentation ToLabel(A.CharacterSegmentation s)
    {
        return new L.CharacterSegmentation(
            s.Status,
            s.Reason,
            s.Basis,
            s.PhysicalCount,
            s.Characters.Select(c => new L.CharacterPatch(
                c.Character,
                c.TokenIndex,
                ToLabel(c.Bounds),
                ToLabel(c.Patch),
                c.NeighborInkRemoved
            ))
        );
    }

    /// <summary>转换实际内容，并保留既有解码契约中网格角点的像素中心约定。</summary>
    /// <param name = "s">中立解码观测，网格角点采用像素边缘坐标。</param>
    public static L.BarcodeObservation ToLabel(A.BarcodeObservation s)
    {
        return new L.BarcodeObservation(
            s.Text,
            s.Format,
            ToLabel(s.Bounds),
            s.ModuleGrid == null
                ? null
                : new L.BarcodeModuleGrid(
                    s.ModuleGrid.Dimension,
                    s.ModuleGrid.Corners.Select(v => v - .5),
                    s.ModuleGrid.SampledModules
                )
        );
    }

    /// <summary>将既有像素中心网格角点转换为中立的像素边缘坐标。</summary>
    /// <param name = "s">标签解码观测，网格角点采用既有像素中心坐标。</param>
    public static A.BarcodeObservation ToVision(L.BarcodeObservation s)
    {
        return new A.BarcodeObservation(
            s.Text,
            s.Format,
            ToVision(s.Bounds),
            s.ModuleGrid == null
                ? null
                : new A.BarcodeModuleGrid(
                    s.ModuleGrid.Dimension,
                    s.ModuleGrid.Corners.Select(v => v + .5),
                    s.ModuleGrid.SampledModules
                )
        );
    }

    /// <summary>复制算法专用印刷设置，不改变单位。</summary>
    /// <param name = "o">标签侧局部印刷阈值及开关。</param>
    public static A.BarcodePrintOptions ToVision(L.BarcodePrintOptions o)
    {
        return new A.BarcodePrintOptions(
            o.Enabled,
            o.MinimumArea,
            o.MinimumFraction,
            o.EdgeTolerance,
            o.CheckQrQuietZone,
            o.DetectInkLoss,
            o.MinimumInkLoss
        );
    }

    /// <summary>将中立证据角色映射到既有报告，最终业务NG由ROI策略决定。</summary>
    /// <param name = "f">中立算法证据，保留阻断角色和原图定位。</param>
    public static L.InspectionFinding ToLabel(A.QualityFinding f)
    {
        return new L.InspectionFinding(
            f.Code,
            f.Message,
            f.Kind == A.EQualityFindingKind.Defect ? L.EInspectionVerdict.Ng
                : f.Kind == A.EQualityFindingKind.Blocker ? L.EInspectionVerdict.Review
                : L.EInspectionVerdict.Ok,
            f.Bounds.HasValue ? ToLabel(f.Bounds.Value) : (L.PixelRect?)null,
            f.AreaPixels
        ).WithExecutionBlocker(f.Kind == A.EQualityFindingKind.Blocker);
    }

    /// <summary>保留原始CTC观测、置信度和实际模型标识。</summary>
    /// <param name = "r">中立OCR结果，包含全部CTC观测及模型哈希。</param>
    public static L.TextLineRecognition ToLabel(A.TextLineRecognition r)
    {
        return new L.TextLineRecognition(
            ToLabel(r.Bounds),
            r.ModelSha256,
            r.InputWidth,
            r.ContentWidth,
            r.Steps.Select(s => new L.CtcStep(s.ClassIndex, s.Confidence)),
            r.Tokens.Select(t => new L.CtcToken(t.Text, t.Start, t.End, t.Confidence))
        );
    }
}
