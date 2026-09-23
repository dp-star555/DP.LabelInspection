using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class InspectionTests
{
    private sealed class IncompleteBackend : IInspectionBackend
    {
        public int DisposeCalls { get; private set; }
        public string Name => "contract test adapter";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.Quality;

        public BackendAnalysis Analyze(InspectionRequest request, CancellationToken cancellationToken)
        {
            return new BackendAnalysis(255, 1000, Array.Empty<RegionInspectionResult>());
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
