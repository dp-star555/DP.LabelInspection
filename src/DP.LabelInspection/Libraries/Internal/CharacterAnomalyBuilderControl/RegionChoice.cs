using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

public sealed partial class CharacterAnomalyBuilderControl
{
    /// <summary>文字ROI下拉项：来自配方，或在画布上手画的文字行（只用于提取样本，不写回配方）。</summary>
    private sealed class RegionChoice
    {
        internal RegionChoice(InspectionRegion region, bool adHoc = false)
        {
            Region = region;
            AdHoc = adHoc;
        }

        internal InspectionRegion Region { get; }

        internal bool AdHoc { get; }

        internal string Name => Region.Name;

        public override string ToString()
        {
            return Region.Name
                + (
                    AdHoc ? "（手画）"
                    : Region.SingleLine ? ""
                    : "（未声明单行）"
                );
        }
    }
}
