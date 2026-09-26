using System.Collections.Generic;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 异常模型库（方法B）的版本化管理，与字库一致：每次修改发布新的不可变版本，按调用方基于的版本拒绝过期编辑，
/// 归档不删除历史，配方绑定精确版本而不自动升级。
/// </summary>
public interface IAnomalyLibraryManager : IAnomalyLibraryRepository
{
    /// <summary>列出模型库最新版本信息。</summary>
    /// <param name = "includeArchived">是否包含已归档的库。</param>
    IReadOnlyList<AnomalyLibraryInfo> ListAnomalyLibraries(bool includeArchived = false);

    /// <summary>使用新标识创建空模型库，返回标识（版本1）。</summary>
    /// <param name = "name">显示名称。</param>
    string CreateAnomalyLibrary(string name);

    /// <summary>添加或替换一个模型，发布新版本并返回新版本号。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "model">训练得到的模型条目。</param>
    /// <param name = "replaceExisting">同键已存在时是否替换；false时拒绝。</param>
    /// <param name = "provenanceJson">可选来源证据JSON（训练图、配方、操作者等）。</param>
    int PutAnomalyModel(
        string id,
        int expectedRevision,
        AnomalyModelEntry model,
        bool replaceExisting = false,
        string? provenanceJson = null
    );

    /// <summary>一次添加或替换多个模型（例如一批字符模型），只发布一个新版本并返回新版本号。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "models">模型条目，键须唯一。</param>
    /// <param name = "replaceExisting">同键已存在时是否替换；false时拒绝整批。</param>
    /// <param name = "provenanceJson">可选来源证据JSON，记录到每个模型。</param>
    int PutAnomalyModels(
        string id,
        int expectedRevision,
        IEnumerable<AnomalyModelEntry> models,
        bool replaceExisting = false,
        string? provenanceJson = null
    );

    /// <summary>移除一个模型，发布新版本并返回新版本号。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本。</param>
    /// <param name = "key">要移除的模型键。</param>
    int RemoveAnomalyModel(string id, int expectedRevision, string key);

    /// <summary>通过发布新版本归档或取消归档，历史版本仍可读取。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本。</param>
    /// <param name = "archived">true归档，false取消归档。</param>
    int ArchiveAnomalyLibrary(string id, int expectedRevision, bool archived = true);

    /// <summary>导出完整可移植版本（内嵌模型字节）。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "revision">固定的导出版本号。</param>
    string ExportAnomalyLibrary(string id, int revision);

    /// <summary>把导出文档导入为新模型库（新标识、版本1），不修改任何配方绑定。</summary>
    /// <param name = "json">导出的完整文档。</param>
    string ImportAnomalyLibrary(string json);
}
