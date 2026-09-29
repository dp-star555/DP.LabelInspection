using System;
using System.Collections.Generic;
using System.Threading;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class InspectionTests
{
    /// <summary>只经公开契约实现的非原生后台：每个阶段都不做图像工作，记录释放次数。</summary>
    private sealed class ContractBackend : IInspectionBackend, IRoiInspectionSession
    {
        private bool _sessionOpen;

        public int DisposeCalls { get; private set; }
        public int Sessions { get; private set; }
        public string Name => "contract test adapter";
        public EInspectionCapabilities Capabilities => EInspectionCapabilities.None;
        public int OffsetX => 0;
        public int OffsetY => 0;

        public IRoiInspectionSession OpenSession(InspectionRequest request)
        {
            Sessions++;
            _sessionOpen = true;
            return this;
        }

        public bool QualityNeedsReading(InspectionRegion region)
        {
            return false;
        }

        public IReadOnlyList<InspectionFinding> Validate(
            InspectionRegion region,
            bool readRequired,
            bool qualityRequired,
            CancellationToken token
        )
        {
            return Array.Empty<InspectionFinding>();
        }

        public InspectionRegion Locate(InspectionRegion region, CancellationToken token)
        {
            return region;
        }

        public RegionInspectionResult Read(InspectionRegion region, CancellationToken token)
        {
            return new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>());
        }

        public RoiQualityMeasurement InspectQuality(
            InspectionRegion region,
            RegionInspectionResult reading,
            CancellationToken token
        )
        {
            return new RoiQualityMeasurement(
                new RegionInspectionResult(region.Name, Array.Empty<InspectionFinding>()),
                true
            );
        }

        /// <summary>先结束本轮会话；会话已结束时才是后台本身的释放。</summary>
        public void Dispose()
        {
            if (_sessionOpen)
            {
                _sessionOpen = false;
                return;
            }

            DisposeCalls++;
        }
    }
}
