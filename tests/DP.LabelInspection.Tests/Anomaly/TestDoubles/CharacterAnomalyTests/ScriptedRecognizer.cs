using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class CharacterAnomalyTests
{
    /// <summary>按图像返回预设读数的OCR替身（每个字符一个CTC标记，置信度0.95）。</summary>
    private sealed class ScriptedRecognizer : ITextLineRecognizer
    {
        private readonly Dictionary<ImageFrame, string> _texts = new Dictionary<ImageFrame, string>();

        internal void Set(ImageFrame frame, string text)
        {
            _texts[frame] = text;
        }

        public TextLineRecognition Recognize(ImageFrame frame, PixelRect bounds, CancellationToken token)
        {
            string text = _texts[frame];
            return new TextLineRecognition(
                bounds,
                "scripted",
                320,
                text.Length * 8,
                Enumerable.Range(0, text.Length * 8).Select(_ => new CtcStep(1, .95f)),
                text.Select((c, i) => new CtcToken(c.ToString(), i * 8, i * 8 + 8, .95f))
            );
        }

        public void Dispose() { }
    }
}
