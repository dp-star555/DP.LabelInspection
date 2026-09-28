using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

public sealed partial class CharacterAnomalyTests
{
    /// <summary>按图像返回预设读数的OCR替身（每个字符一个CTC标记，置信度0.95）。</summary>
    private sealed class ScriptedRecognizer : A.ITextLineRecognizer
    {
        private string? _text;

        internal void Set(string text) => _text = text;

        public A.TextLineRecognition Recognize(
            DP.Vision.IImageSource frame, A.PixelBounds bounds, CancellationToken token)
        {
            string text = _text ?? throw new System.InvalidOperationException("No scripted text.");
            return new A.TextLineRecognition(
                bounds,
                "scripted",
                320,
                text.Length * 8,
                Enumerable.Range(0, text.Length * 8).Select(_ => new A.CtcStep(1, .95f)),
                text.Select((c, i) => new A.CtcToken(c.ToString(), i * 8, i * 8 + 8, .95f))
            );
        }

        public void Dispose() { }
    }
}
