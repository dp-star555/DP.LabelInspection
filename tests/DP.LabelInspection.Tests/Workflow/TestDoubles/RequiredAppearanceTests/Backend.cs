using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class RequiredAppearanceTests
{
    private sealed class Backend : IInspectionBackend
    {
        private readonly RegionInspectionResult[] _regions;

        internal Backend(params RegionInspectionResult[] regions)
        {
            _regions = regions;
        }

        public string Name => "test";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;

        public BackendAnalysis Analyze(InspectionRequest r, CancellationToken t)
        {
            return new BackendAnalysis(0, 0, _regions);
        }

        public void Dispose() { }
    }
}
