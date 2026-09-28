using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision;
using A = DP.Vision.Algorithms;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

public sealed partial class OpenCvInspectionBackend
{
    private sealed partial class RoiSession
    {
        /// <summary>按配置执行定位，返回实际原图坐标中的ROI。</summary>
        /// <param name = "r">定位前的ROI快照。</param>
        /// <param name = "token">协作式取消标记。</param>
        public InspectionRegion Locate(InspectionRegion r, CancellationToken token)
        {
            Alive();
            token.ThrowIfCancellationRequested();
            if (
                _request.Recipe.Mode == EInspectionMode.Template
                && _request.Recipe.Alignment == EAlignmentMode.Translation
                && !_located
            )
            {
                _located = true;
                using var actualSource = _request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual);
                using var referenceSource =
                    _request.VisionReference?.Retain() ?? Bridge.ToVision(_request.Reference!);
                var offset = Register(actualSource, referenceSource, token);
                if (offset == null)
                {
                    _alignmentFailed = true;
                }
                else
                {
                    OffsetX = (int)Math.Round(offset.OffsetX);
                    OffsetY = (int)Math.Round(offset.OffsetY);
                }
            }

            if (_alignmentFailed)
            {
                throw new InvalidOperationException("alignment_failed: 未取得可靠定位。");
            }

            long x = (long)r.Bounds.X + OffsetX,
                y = (long)r.Bounds.Y + OffsetY;
            if (
                x < 0
                || y < 0
                || x + r.Bounds.Width > _request.ImageWidth
                || y + r.Bounds.Height > _request.ImageHeight
            )
            {
                throw new InvalidOperationException("定位后ROI超出原图。");
            }

            return new InspectionRegion(
                r.Name,
                r.Kind,
                new PixelRect((int)x, (int)y, r.Bounds.Width, r.Bounds.Height),
                r.SingleLine,
                r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode ? r.Field : null,
                r.Anomaly
            ).WithTasks(r.Tasks);
        }

        /// <summary>
        /// 在配方第一个固定ROI（至少20×20）上做受限平移配准，忽略区不参与；没有合适的固定ROI或配准不可信时返回null。
        /// </summary>
        private A.TranslationRegistrationResult? Register(
            IImageSource actual,
            IImageSource reference,
            CancellationToken token
        )
        {
            var fixedRegion = _request.Recipe.Regions.FirstOrDefault(v => v.Kind == ERegionKind.Fixed);
            if (fixedRegion == null || fixedRegion.Bounds.Width < 20 || fixedRegion.Bounds.Height < 20)
            {
                return null;
            }

            var bounds = Bridge.ToVision(fixedRegion.Bounds);
            var mask = A.InspectionMask.Compose(
                reference,
                new Geometry[] { bounds.ToGeometry() },
                _request
                    .Recipe.Regions.Where(v => v.Kind == ERegionKind.Ignore)
                    .Select(v => Bridge.ToVision(v.Bounds).Intersect(bounds))
                    .Where(b => b != null)
                    .Select(b => (Geometry)b!.Value.ToGeometry()),
                token
            );
            var result = _owner._registrar.Register(actual, reference, bounds, mask, null, token);
            return result.Found ? result : null;
        }
    }
}
