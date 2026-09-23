using System.Collections.Generic;

namespace DP.LabelInspection.Contracts;

/// <summary>面向UI的独立字符字库操作，版本参数用于乐观并发控制。</summary>
public interface IGlyphLibraryManager : IGlyphLibraryRepository
{
    /// <summary>列出活动类别或全部类别的最新版本信息。</summary>
    /// <param name = "includeArchived">是否同时返回已归档类别，默认不返回。</param>
    IReadOnlyList<GlyphLibraryInfo> ListLibraries(bool includeArchived = false);

    /// <summary>创建空类别。</summary>
    /// <param name = "name">新类别的显示名称。</param>
    string CreateLibrary(string name);

    /// <summary>以新标识导入，不修改现有绑定。</summary>
    /// <param name = "json">完整可移植字库JSON，导入为新标识，不重绑配方。</param>
    string ImportLibrary(string json);

    /// <summary>导出固定版本的可移植数据。</summary>
    /// <param name = "id">要导出的类别标识。</param>
    /// <param name = "revision">固定的导出版本号。</param>
    string ExportLibrary(string id, int revision);

    /// <summary>发布一个独立字符参考。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "character">大小写敏感的独立字符标签。</param>
    /// <param name = "image">独立不可变参考图块。</param>
    /// <param name = "binarization">二值化模式：otsu自动阈值或fixed固定阈值。</param>
    /// <param name = "provenanceJson">可选来源证据JSON。</param>
    int PutGlyph(
        string id,
        int expectedRevision,
        string character,
        ImageFrame image,
        string binarization = "otsu",
        string? provenanceJson = null
    );

    /// <summary>将明确声明的规则字表以一个原子新版本导入为独立标签。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "sheet">明确按规则等格排版的不可变字表图。</param>
    /// <param name = "alphabet">按单元排列顺序提供的独立字符序列，不能有重复标签。</param>
    /// <param name = "rows">字表行数。</param>
    /// <param name = "columns">字表列数。</param>
    /// <param name = "padding">每个单元向内裁去的边距，单位为字表原图像素。</param>
    /// <param name = "binarization">各参考采用的二值化模式，otsu或fixed。</param>
    int PutSheet(
        string id,
        int expectedRevision,
        ImageFrame sheet,
        string alphabet,
        int rows,
        int columns,
        int padding = 1,
        string binarization = "otsu"
    );

    /// <summary>通过新版本删除一个参考。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "character">要移除的大小写敏感字符标签。</param>
    int RemoveGlyph(string id, int expectedRevision, string character);

    /// <summary>归档或取消归档，不删除历史。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "archived">true归档，false取消归档，均通过发布新版本实现。</param>
    int Archive(string id, int expectedRevision, bool archived = true);
}
