namespace DP.LabelInspection.Contracts;

/// <summary>供宿主或UI选择的异常模型库最新版本信息。</summary>
public sealed class AnomalyLibraryInfo
{
    /// <summary>创建列表项。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "name">显示名称。</param>
    /// <param name = "revision">最新已发布不可变版本号。</param>
    /// <param name = "archived">是否已归档，归档不删除历史。</param>
    /// <param name = "models">最新版本中的模型数量。</param>
    public AnomalyLibraryInfo(string id, string name, int revision, bool archived, int models)
    {
        Id = id;
        Name = name;
        Revision = revision;
        Archived = archived;
        Models = models;
    }

    /// <summary>模型库标识。</summary>
    public string Id { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>最新不可变版本。</summary>
    public int Revision { get; }

    /// <summary>归档时从活动选择中隐藏。</summary>
    public bool Archived { get; }

    /// <summary>最新版本中的模型数量。</summary>
    public int Models { get; }

    /// <summary>供用户阅读的选择文本。</summary>
    public override string ToString()
    {
        return Name + " · r" + Revision + " · " + Models + "个模型" + (Archived ? " [archived]" : "");
    }
}
