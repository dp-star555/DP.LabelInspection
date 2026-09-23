using System.Collections.Generic;

namespace DP.LabelInspection.Contracts;

/// <summary>供宿主或UI选择的轻量类别最新版本信息。</summary>
public sealed class GlyphLibraryInfo
{
    /// <summary>创建类别列表项。</summary>
    /// <param name = "id">字库类别标识。</param>
    /// <param name = "name">类别显示名称。</param>
    /// <param name = "revision">最新已发布不可变版本号。</param>
    /// <param name = "archived">是否已经归档，归档不删除历史。</param>
    public GlyphLibraryInfo(string id, string name, int revision, bool archived)
    {
        Id = id;
        Name = name;
        Revision = revision;
        Archived = archived;
    }

    /// <summary>类别标识。</summary>
    public string Id { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>最新不可变版本。</summary>
    public int Revision { get; }

    /// <summary>归档时从活动选择中隐藏。</summary>
    public bool Archived { get; }

    /// <summary>供用户阅读的选择文本。</summary>
    public override string ToString()
    {
        return Name + " · r" + Revision + (Archived ? " [archived]" : "");
    }
}
