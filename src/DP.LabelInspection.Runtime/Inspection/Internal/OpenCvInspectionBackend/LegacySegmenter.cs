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
    private sealed class LegacySegmenter : A.ICharacterSegmenter
    {
        private readonly ICharacterSegmenter _legacy;

        internal LegacySegmenter(ICharacterSegmenter legacy)
        {
            _legacy = legacy;
        }

        /// <summary>桥接物理字符分割并复制证据，不强制按身份提示切字。</summary>
        /// <param name = "frame">借用的中立原始图像。</param>
        /// <param name = "bounds">原图整数单行范围。</param>
        /// <param name = "text">真实身份提示，不强行凑齐分割数量。</param>
        /// <param name = "token">协作式取消标记。</param>
        public A.CharacterSegmentation Segment(
            IImageSource frame,
            A.PixelBounds bounds,
            string text,
            CancellationToken token = default
        )
        {
            return Convert(_legacy.Segment(Bridge.ToLabel(frame), Bridge.ToLabel(bounds), text, token));
        }

        /// <summary>桥接明确声明的等宽单元分割，不用作粘连回退。</summary>
        /// <param name = "frame">借用的中立原始图像。</param>
        /// <param name = "bounds">明确声明等宽布局的原图整数范围。</param>
        /// <param name = "expected">调用方明确确认的等格标签序列。</param>
        public A.CharacterSegmentation EqualCells(IImageSource frame, A.PixelBounds bounds, string expected)
        {
            return Convert(_legacy.EqualCells(Bridge.ToLabel(frame), Bridge.ToLabel(bounds), expected));
        }

        private static A.CharacterSegmentation Convert(CharacterSegmentation result)
        {
            var owned = new List<A.CharacterPatch>();
            try
            {
                foreach (var c in result.Characters)
                {
                    owned.Add(
                        new A.CharacterPatch(
                            c.Character,
                            c.TokenIndex,
                            Bridge.ToVision(c.Bounds),
                            Bridge.ToVision(c.Patch),
                            c.NeighborInkRemoved
                        )
                    );
                }

                return new A.CharacterSegmentation(
                    result.Status,
                    result.Reason,
                    result.Basis,
                    result.PhysicalCount,
                    owned
                );
            }
            catch
            {
                foreach (var c in owned)
                {
                    c.Dispose();
                }

                throw;
            }
        }
    }
}
