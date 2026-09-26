using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

public sealed partial class AnomalyLibraryControl
{
    private sealed class RegionItem
    {
        internal RegionItem(InspectionRegion region)
        {
            Region = region;
        }

        internal InspectionRegion Region { get; }

        public override string ToString()
        {
            return Region.Name
                + " · "
                + UiText.Get("Kind" + Region.Kind)
                + (Region.Anomaly == null ? "" : " · 已绑定");
        }
    }
}
