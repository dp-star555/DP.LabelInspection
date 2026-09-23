using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class GlyphDraftSessionTests
{
    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "glyph-draft-" + Guid.NewGuid().ToString("N")
        );
        internal readonly InspectionStore Value;

        internal Store()
        {
            Value = new InspectionStore(_root, new OpenCvImageCodec());
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}
