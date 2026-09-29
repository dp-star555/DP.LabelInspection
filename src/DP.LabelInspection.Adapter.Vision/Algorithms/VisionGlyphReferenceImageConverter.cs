using DP.LabelInspection.Contracts;
using DP.Vision;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>
/// 默认参考图转换：整帧复制为 Vision 图像租约，行为与
/// <see cref="AlgorithmContractAdapter.ToVision(DP.LabelInspection.Contracts.PixelSnapshot)"/> 完全一致。
/// 转换会复制像素，因此调用方必须缓存结果而不是每次检测重新调用。
/// </summary>
public sealed class VisionGlyphReferenceImageConverter : IGlyphReferenceImageConverter
{
    /// <inheritdoc/>
    public IImageSource Convert(PixelSnapshot image)
    {
        return AlgorithmContractAdapter.ToVision(image);
    }
}
