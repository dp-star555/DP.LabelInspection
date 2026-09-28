using System;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Runtime;

namespace DP.LabelInspection.Tests;

public sealed partial class AnomalyTrainingSessionTests
{
    /// <summary>字符候选替身：按确认文本用真实分割器分割（不需要OCR）。</summary>
    private sealed class Candidates : IGlyphCandidateService
    {
        internal int Calls;

        public Task<GlyphCandidateExtraction> ExtractGlyphCandidatesAsync(
            DP.Vision.IImageSource frame,
            PixelRect bounds,
            string? confirmedText = null,
            CancellationToken token = default
        )
        {
            Calls++;
            string text = confirmedText ?? throw new InvalidOperationException("测试须先设置确认文本。");
            using var measured = new DP.Vision.OpenCv.OpenCvCharacterSegmenter().Segment(
                frame, DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(bounds), text, token);
            return Task.FromResult(new GlyphCandidateExtraction(
                null, confirmedText, DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(measured)
            ));
        }
    }
}
