namespace DP.LabelInspection.Contracts;

/// <summary>原图整数范围（<c>PixelRect</c>，即DP.Vision的PixelBounds）与标签图像快照之间的辅助方法。</summary>
public static class PixelRectExtensions
{
    /// <summary>检查范围是否完整位于标签图像快照内，并拒绝默认空范围。</summary>
    /// <param name = "bounds">原图范围。</param>
    /// <param name = "frame">用于确定边界的图像，只读取尺寸。</param>
    public static bool Fits(this PixelRect bounds, PixelSnapshot frame)
    {
        return bounds.Fits(frame.Width, frame.Height);
    }
}
