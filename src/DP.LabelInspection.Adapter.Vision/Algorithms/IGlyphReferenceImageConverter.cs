using DP.LabelInspection.Contracts;
using DP.Vision;

namespace DP.LabelInspection.Adapter.Vision;

/// <summary>
/// 把固定版本字库里的参考图快照转换为 Vision 图像租约。固定版本不可变，因此同一版本的每个参考图
/// 只需转换一次，由持有缓存的调用方复用；实现必须复制像素，不得把输入快照的缓冲区借出去。
/// </summary>
public interface IGlyphReferenceImageConverter
{
    /// <summary>复制参考图像素并返回独立租约，由调用方负责释放。</summary>
    /// <param name = "image">字库条目里已解码的不可变参考图快照。</param>
    /// <returns>需由调用方Dispose的独立图像租约。</returns>
    IImageSource Convert(PixelSnapshot image);
}
