namespace DP.LabelInspection.Contracts;

/// <summary>明确声明的码族，独立于解码成功与否或是否启用印刷检查。</summary>
public enum EBarcodeKind
{
    /// <summary>历史通用条码ROI，仅依据可靠解码观测推断码族。</summary>
    Auto = 0,

    /// <summary>明确声明的一维条码ROI。</summary>
    OneDimensional = 1,

    /// <summary>明确声明的QR Code ROI，不代表全部二维码制。</summary>
    QrCode = 2,
}
