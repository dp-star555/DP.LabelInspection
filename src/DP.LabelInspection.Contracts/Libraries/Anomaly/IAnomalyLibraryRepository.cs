namespace DP.LabelInspection.Contracts;

/// <summary>按固定版本读取异常模型库（方法B）的仓库接口，不包含文件系统或厂商类型。</summary>
public interface IAnomalyLibraryRepository
{
    /// <summary>加载精确版本，不存在则抛出异常，不能替换为最新版本。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "revision">要求加载的精确版本号。</param>
    AnomalyLibrarySnapshot LoadAnomalyLibrary(string id, int revision);
}
