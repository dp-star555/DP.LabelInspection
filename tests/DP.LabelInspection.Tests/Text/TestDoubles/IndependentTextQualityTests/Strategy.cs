using System;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DP.Vision.Algorithms;

namespace DP.LabelInspection.Tests;

public sealed partial class IndependentTextQualityTests
{
    private sealed class Strategy : A.ITextQualityInspector
    {
        internal int Calls;
        internal bool Complete = true;
        public bool RequiresReferences => false;
        public bool RequiresRecognition => false;

        public A.TextQualityResult Inspect(A.TextQualityRequest r, CancellationToken t = default)
        {
            Calls++;
            return new A.TextQualityResult(
                Complete,
                Complete
                    ? Array.Empty<A.QualityFinding>()
                    : new[]
                    {
                        new A.QualityFinding(
                            "custom_blocker",
                            "not a measured defect",
                            A.EQualityFindingKind.Blocker,
                            r.Bounds,
                            7
                        ),
                    }
            );
        }
    }
}
