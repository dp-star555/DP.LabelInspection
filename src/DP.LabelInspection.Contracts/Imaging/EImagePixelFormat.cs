using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>受支持的紧密排列托管图像布局，不跨接口暴露原生句柄。</summary>
public enum EImagePixelFormat
{
    /// <summary>每像素一个无符号字节。</summary>
    Gray8,

    /// <summary>每像素三个无符号字节，按蓝、绿、红顺序排列。</summary>
    Bgr24,
}
