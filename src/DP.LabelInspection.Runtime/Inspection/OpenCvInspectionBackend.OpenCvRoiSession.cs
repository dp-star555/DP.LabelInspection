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
    /// <inheritdoc/>
    public IRoiInspectionSession OpenSession(InspectionRequest request)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenCvInspectionBackend));
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        // 本后台只接受携带Vision原图租约的请求。旧快照请求在这里明确失败，
        // 而不是在ROI阶段用空值兜底整帧转换——那会复制整帧，并掩盖像素真正来自哪里。
        if (request.VisionSource == null)
        {
            throw new ArgumentException(
                "InspectionRequest must carry a Vision image lease; create it with InspectionRequest.FromVision.",
                nameof(request)
            );
        }

        return new RoiSession(this, request);
    }
}
