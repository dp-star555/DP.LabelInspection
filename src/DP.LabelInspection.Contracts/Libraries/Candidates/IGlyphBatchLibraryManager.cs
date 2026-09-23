using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>可选的多参考原子发布能力，拒绝过期版本修改。</summary>
public interface IGlyphBatchLibraryManager
{
    /// <summary>将所有选定参考发布为一个版本，或全部不发布；拒绝输入重复标签。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方编辑基于的版本，过期则拒绝发布。</param>
    /// <param name = "items">本次原子发布的参考集合，不允许重复标签。</param>
    /// <param name = "replaceExisting">是否明确允许替换已有标签，默认不允许。</param>
    int PutGlyphs(
        string id,
        int expectedRevision,
        IEnumerable<GlyphImportItem> items,
        bool replaceExisting = false
    );
}
