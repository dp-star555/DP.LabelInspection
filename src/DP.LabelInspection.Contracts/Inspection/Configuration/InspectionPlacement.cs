using System;
using V = DP.Vision;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 配方（标签）坐标到本帧原图坐标的仿射放置：<c>x' = M11·x + M12·y + Tx</c>，<c>y' = M21·x + M22·y + Ty</c>。
/// 坐标为连续像素坐标，像素 (i, j) 覆盖 [i, i+1)×[j, j+1)。由宿主的定位结果（模板匹配、坐标系等）提供，
/// 支持平移、旋转和缩放；检测时只对每个 ROI 范围按此放置从原图取样，不变换整张图。
/// </summary>
public sealed class InspectionPlacement
{
    /// <summary>创建放置；矩阵必须有限且可逆。</summary>
    /// <param name="m11">x 对 x' 的系数。</param><param name="m12">y 对 x' 的系数。</param><param name="tx">x' 平移。</param>
    /// <param name="m21">x 对 y' 的系数。</param><param name="m22">y 对 y' 的系数。</param><param name="ty">y' 平移。</param>
    public InspectionPlacement(double m11, double m12, double tx, double m21, double m22, double ty)
    {
        foreach (var value in new[] { m11, m12, tx, m21, m22, ty })
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("Placement must be finite.");
        }

        double determinant = m11 * m22 - m12 * m21;
        if (Math.Abs(determinant) < 1e-9)
            throw new ArgumentException("Placement must be invertible.");
        M11 = m11; M12 = m12; Tx = tx; M21 = m21; M22 = m22; Ty = ty;
    }

    /// <summary>恒等放置（配方坐标即原图坐标）。</summary>
    public static InspectionPlacement Identity { get; } = new InspectionPlacement(1, 0, 0, 0, 1, 0);

    /// <summary>x 对 x' 的系数。</summary>
    public double M11 { get; }
    /// <summary>y 对 x' 的系数。</summary>
    public double M12 { get; }
    /// <summary>x' 平移。</summary>
    public double Tx { get; }
    /// <summary>x 对 y' 的系数。</summary>
    public double M21 { get; }
    /// <summary>y 对 y' 的系数。</summary>
    public double M22 { get; }
    /// <summary>y' 平移。</summary>
    public double Ty { get; }

    /// <summary>是否为整数像素平移（无需重采样，可直接按偏移取像素）。</summary>
    public bool IsIntegerTranslation =>
        M11 == 1 && M12 == 0 && M21 == 0 && M22 == 1 && Tx == Math.Round(Tx) && Ty == Math.Round(Ty);

    /// <summary>配方 X 轴在原图中的方向角（度，图像 Y 向下时顺时针为正）。</summary>
    public double RotationDegrees => Math.Atan2(M21, M11) * 180 / Math.PI;

    /// <summary>平均缩放（原图像素/配方像素）。</summary>
    public double Scale => Math.Sqrt(Math.Abs(M11 * M22 - M12 * M21));

    /// <summary>
    /// 按放置把整个标签区域摆正为 <paramref name="width"/>×<paramref name="height"/> 的图（双线性），用于配方制作、参考图和显示；
    /// 生产检测不需要调用它（请求只对ROI范围取样）。超出原图的部分取边缘像素。
    /// </summary>
    /// <param name="actual">原图。</param><param name="width">标签宽（配方像素）。</param><param name="height">标签高（配方像素）。</param>
    /// <returns>调用方拥有的新帧，帧标识为原图标识加 <c>#label</c>。</returns>
    public V.ImageFrame Rectify(V.ImageFrame actual, int width, int height)
    {
        if (actual == null) throw new ArgumentNullException(nameof(actual));
        if (width <= 0 || height <= 0 || (long)width * height > 16000000) throw new ArgumentOutOfRangeException(nameof(width), "Label size must be positive and at most 16M pixels.");
        return PlacedSampler.Sample(actual, width, height, this, new[] { (0, 0, width, height) }, actual.FrameId + "#label");
    }

    /// <summary>把配方坐标映射到原图坐标。</summary>
    /// <param name="x">配方 X。</param><param name="y">配方 Y。</param>
    public (double X, double Y) Map(double x, double y) => (M11 * x + M12 * y + Tx, M21 * x + M22 * y + Ty);
}
