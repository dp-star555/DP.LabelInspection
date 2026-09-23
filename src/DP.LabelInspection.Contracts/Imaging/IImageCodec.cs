using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>供存储和宿主使用的可移植图像编解码接口。</summary>
public interface IImageCodec
{
    /// <summary>解码大小受限的图像字节。</summary>
    /// <param name = "bytes">待解码的完整图像文件字节，不是裸像素。</param>
    ImageFrame Decode(byte[] bytes);

    /// <summary>编码PNG而不修改图像帧。</summary>
    /// <param name = "frame">要编码的不可变图像帧。</param>
    byte[] EncodePng(ImageFrame frame);
}
