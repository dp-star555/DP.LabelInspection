using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>可选的同步后台能力，宿主引擎将其与检测及释放串行化。</summary>
public interface IGlyphCandidateBackend
{
    /// <summary>提取一条水平单行，可选择使用明确确认的文本代替OCR。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "bounds">水平单行的原图整数范围。</param>
    /// <param name = "confirmedText">可选人工确认文本，null使用OCR，不回写原始读数。</param>
    /// <param name = "token">协作式取消标记。</param>
    GlyphCandidateExtraction ExtractGlyphCandidates(
        ImageFrame frame,
        PixelRect bounds,
        string? confirmedText,
        CancellationToken token
    );
}
