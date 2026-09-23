using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class GlyphQuickLibraryTests
{
    private sealed class Store : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "quick-library-" + Guid.NewGuid().ToString("N")
        );
        internal readonly InspectionStore Value;

        internal Store()
        {
            Value = new InspectionStore(_path, new OpenCvImageCodec());
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, true);
            }
        }
    }
}
