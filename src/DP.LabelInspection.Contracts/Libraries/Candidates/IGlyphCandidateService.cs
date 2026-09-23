using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>面向UI的可选独立字库制作能力，不依赖检测规则或已有字库。</summary>
public interface IGlyphCandidateService
{
    /// <summary>生成候选，不比较或修改字库；与检测共用引擎执行关卡。</summary>
    /// <param name = "frame">不可变原始图像。</param>
    /// <param name = "bounds">水平单行的原图整数范围。</param>
    /// <param name = "confirmedText">可选人工确认文本，null使用OCR，不回写原始读数。</param>
    /// <param name = "token">协作式取消标记。</param>
    Task<GlyphCandidateExtraction> ExtractGlyphCandidatesAsync(
        ImageFrame frame,
        PixelRect bounds,
        string? confirmedText = null,
        CancellationToken token = default
    );
}
