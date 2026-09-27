using System;
using System.Collections.Generic;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 检测后端内的异常模型缓存：库版本不可变，按“库标识+版本”缓存最近载入的版本，解析后的模型按模型字节SHA256缓存。
/// 否则每次检测都要从磁盘读库、重新解析全部模型，模型对象每次都是新的，算法与缺墨检查按模型缓存的参考数据也无法复用。
/// 线程安全。
/// </summary>
internal sealed class AnomalyModelCache
{
    private const int LibraryCapacity = 8;
    private const int ModelCapacity = 4096;

    private readonly object _gate = new object();
    private readonly LinkedList<(string Id, int Revision, AnomalyLibrarySnapshot Library)> _libraries =
        new LinkedList<(string, int, AnomalyLibrarySnapshot)>();
    private readonly Dictionary<string, PatchAnomalyModel> _models = new Dictionary<
        string,
        PatchAnomalyModel
    >(StringComparer.Ordinal);

    /// <summary>取库的某个版本；未缓存时由<paramref name = "repository"/>载入（异常原样抛出，不缓存失败）。</summary>
    internal AnomalyLibrarySnapshot Library(IAnomalyLibraryRepository repository, string id, int revision)
    {
        lock (_gate)
        {
            for (var node = _libraries.First; node != null; node = node.Next)
            {
                if (
                    node.Value.Revision == revision
                    && string.Equals(node.Value.Id, id, StringComparison.Ordinal)
                )
                {
                    _libraries.Remove(node);
                    _libraries.AddFirst(node);
                    return node.Value.Library;
                }
            }
        }

        var library = repository.LoadAnomalyLibrary(id, revision);
        lock (_gate)
        {
            _libraries.AddFirst((id, revision, library));
            while (_libraries.Count > LibraryCapacity)
            {
                _libraries.RemoveLast();
            }
        }

        return library;
    }

    /// <summary>解析条目中的模型（同一模型字节只解析一次）。</summary>
    internal PatchAnomalyModel Model(AnomalyModelEntry entry)
    {
        lock (_gate)
        {
            if (_models.TryGetValue(entry.Sha256, out var cached))
            {
                return cached;
            }
        }

        var model = PatchAnomalyModel.FromBytes(entry.CopyModel());
        lock (_gate)
        {
            if (_models.Count >= ModelCapacity)
            {
                _models.Clear();
            }

            _models[entry.Sha256] = model;
        }

        return model;
    }
}
