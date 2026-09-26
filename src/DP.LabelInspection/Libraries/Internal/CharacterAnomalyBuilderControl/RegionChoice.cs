using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

public sealed partial class CharacterAnomalyBuilderControl
{
    /// <summary>文字ROI下拉项。</summary>
    private sealed class RegionChoice
    {
        internal RegionChoice(InspectionRegion region)
        {
            Region = region;
        }

        internal InspectionRegion Region { get; }

        public override string ToString()
        {
            return Region.Name + (Region.SingleLine ? "" : "（未声明单行）");
        }
    }
}
