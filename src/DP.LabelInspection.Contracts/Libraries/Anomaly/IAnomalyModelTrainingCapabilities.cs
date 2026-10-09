namespace DP.LabelInspection.Contracts;

/// <summary>可选的训练附加能力；不把旧Patch缺墨模型强加给厂商原生资产。</summary>
public interface IAnomalyModelTrainingCapabilities
{
    /// <summary>发布的逐字符模型是否包含兼容的独立ink_loss模型。</summary>
    bool SupportsInkLoss { get; }
}
