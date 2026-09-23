using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace DP.LabelInspection.Tests;

public sealed partial class FullInspectionTests
{
    private sealed class TempStore : IDisposable
    {
        internal string Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dp-inspection-tests-" + Guid.NewGuid().ToString("N")
        );
        internal InspectionStore Store;

        internal TempStore()
        {
            Store = new InspectionStore(Path, new OpenCvImageCodec());
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
