using System;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime.Recognition;

public sealed partial class OnnxTextLineRecognizer
{
    private sealed class Preprocessor : DP.Vision.Algorithms.ITextLinePreprocessor
    {
        private readonly ITextLinePreprocessor _legacy;

        internal Preprocessor(ITextLinePreprocessor legacy)
        {
            _legacy = legacy;
        }

        /// <summary>将中立输入桥接到宿主选择的预处理器，返回独立归一化输入。</summary>
        /// <param name = "frame">借用的中立图像租约，调用期间须有效。</param>
        /// <param name = "bounds">原图整数单行范围。</param>
        /// <param name = "token">协作式取消标记。</param>
        public DP.Vision.Algorithms.TextLineInput Prepare(
            DP.Vision.IImageSource frame,
            DP.Vision.Algorithms.PixelBounds bounds,
            CancellationToken token
        )
        {
            var result = _legacy.Prepare(Bridge.ToLabel(frame), Bridge.ToLabel(bounds), token);
            return new DP.Vision.Algorithms.TextLineInput(
                result.Width,
                result.ContentWidth,
                result.CopyValues()
            );
        }
    }
}
