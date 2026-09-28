using System;
using System.Collections.Generic;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 按“库标识+版本”缓存最近载入的不可变库版本（最近使用优先，超出容量淘汰最久未用的）。
/// 版本一经发布不再改变，因此同一版本只需从存储载入一次。载入失败原样抛出，不缓存失败。线程安全。
/// </summary>
/// <typeparam name = "T">库快照类型。</typeparam>
internal sealed class PinnedRevisionCache<T>
    where T : class
{
    private readonly int _capacity;
    private readonly object _gate = new object();
    private readonly LinkedList<(string Id, int Revision, T Value)> _entries =
        new LinkedList<(string, int, T)>();

    internal PinnedRevisionCache(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>取某库的某个版本；未缓存时调用<paramref name = "load"/>载入。</summary>
    /// <param name = "id">库标识。</param>
    /// <param name = "revision">固定版本号。</param>
    /// <param name = "load">从存储载入该版本。</param>
    internal T Get(string id, int revision, Func<T> load)
    {
        lock (_gate)
        {
            for (var node = _entries.First; node != null; node = node.Next)
            {
                if (node.Value.Revision == revision && string.Equals(node.Value.Id, id, StringComparison.Ordinal))
                {
                    _entries.Remove(node);
                    _entries.AddFirst(node);
                    return node.Value.Value;
                }
            }
        }

        var value = load();
        lock (_gate)
        {
            _entries.AddFirst((id, revision, value));
            while (_entries.Count > _capacity)
            {
                _entries.RemoveLast();
            }
        }

        return value;
    }
}
