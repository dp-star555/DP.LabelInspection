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
    /// <inheritdoc/>
    public IRoiInspectionSession OpenSession(InspectionRequest request)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenCvInspectionBackend));
        }

        return new RoiSession(this, request ?? throw new ArgumentNullException(nameof(request)));
    }
}
