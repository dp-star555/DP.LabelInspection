using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>工作台左键拖动新建ROI时的区域类型选项；宿主可放进自己的下拉框。</summary>
public sealed class RegionDrawKind
{
    internal RegionDrawKind(object item, string text)
    {
        Item = item;
        Text = text;
        Kind = item is EBarcodeKind ? ERegionKind.Barcode : (ERegionKind)item;
        BarcodeKind = item as EBarcodeKind?;
    }

    internal object Item { get; }

    /// <summary>显示文字。</summary>
    public string Text { get; }

    /// <summary>新建ROI的区域类型。</summary>
    public ERegionKind Kind { get; }

    /// <summary>条码ROI声明的码制；null为自动或非条码。</summary>
    public EBarcodeKind? BarcodeKind { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
