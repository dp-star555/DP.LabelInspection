using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>固定版本的不可变异常模型库（方法B），按模型键序数查询。</summary>
public sealed class AnomalyLibrarySnapshot
{
    /// <summary>将模型复制到序数比较字典。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "revision">正整数不可变版本号。</param>
    /// <param name = "name">显示名称。</param>
    /// <param name = "models">模型条目，键须唯一。</param>
    public AnomalyLibrarySnapshot(string id, int revision, string name, IEnumerable<AnomalyModelEntry> models)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Revision = revision;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Models = new ReadOnlyDictionary<string, AnomalyModelEntry>(
            (models ?? throw new ArgumentNullException(nameof(models))).ToDictionary(
                m => m.Key,
                StringComparer.Ordinal
            )
        );
    }

    /// <summary>模型库标识。</summary>
    public string Id { get; }

    /// <summary>不可变版本号。</summary>
    public int Revision { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>模型查询表。</summary>
    public IReadOnlyDictionary<string, AnomalyModelEntry> Models { get; }
}
