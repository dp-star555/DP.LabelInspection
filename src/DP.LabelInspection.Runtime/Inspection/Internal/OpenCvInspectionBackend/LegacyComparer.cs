using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision;
using OpenCvSharp;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

public sealed partial class OpenCvInspectionBackend
{
    private sealed class LegacyComparer : A.IGlyphComparer
    {
        private readonly IGlyphComparer _legacy;
        private readonly Dictionary<IImageSource, GlyphReference> _references;

        internal LegacyComparer(IGlyphComparer legacy, Dictionary<IImageSource, GlyphReference> references)
        {
            _legacy = legacy;
            _references = references;
        }

        /// <summary>桥接独立单字比较，返回具有独立租约的归一化证据。</summary>
        /// <param name = "actual">借用的实际单字图像。</param>
        /// <param name = "reference">借用的独立参考图像。</param>
        /// <param name = "options">二值化及归一化容差设置。</param>
        /// <param name = "token">协作式取消标记。</param>
        public A.GlyphComparisonResult Compare(
            IImageSource actual,
            IImageSource reference,
            A.GlyphComparisonOptions options,
            CancellationToken token = default
        )
        {
            token.ThrowIfCancellationRequested();
            var result = _legacy.Compare(
                Bridge.ToLabel(actual),
                _references[reference],
                options.Threshold,
                options.Tolerance
            );
            token.ThrowIfCancellationRequested();
            using var a = Bridge.ToVision(result.Actual);
            using var r = Bridge.ToVision(result.Reference);
            using var d = Bridge.ToVision(result.Delta);
            return new A.GlyphComparisonResult(
                result.Status == "compared"
                    ? A.EAlgorithmStatus.Completed
                    : A.EAlgorithmStatus.InsufficientEvidence,
                result.Status == "compared" ? "" : result.Status,
                result.Difference,
                result.Missing,
                result.Extra,
                a,
                r,
                d
            );
        }
    }
}
