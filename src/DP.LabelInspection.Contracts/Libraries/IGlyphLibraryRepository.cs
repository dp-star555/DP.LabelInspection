using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>不包含文件系统或厂商类型的仓库接口。</summary>
public interface IGlyphLibraryRepository
{
    /// <summary>加载精确版本，不存在则抛出异常，不能替换为最新版本。</summary>
    /// <param name = "id">字库类别标识。</param>
    /// <param name = "revision">要求加载的精确版本号，不回退到最新版本。</param>
    GlyphLibrarySnapshot Load(string id, int revision);
}
