using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DP.LabelInspection.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DP.LabelInspection.Storage;

public sealed partial class InspectionStore
{
    private sealed class FrameConverter(IImageCodec codec) : JsonConverter<ImageFrame>
    {
        /// <summary>将不可变图像快照写入JSON，不修改源像素。</summary>
        /// <param name = "writer">调用方拥有的JSON写入器。</param>
        /// <param name = "value">待写入的不可变图像，可为null。</param>
        /// <param name = "serializer">当前JSON序列化上下文。</param>
        public override void WriteJson(JsonWriter writer, ImageFrame? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            new JObject { { "png_base64", Convert.ToBase64String(codec.EncodePng(value)) } }.WriteTo(writer);
        }

        /// <summary>读取并校验JSON图像，创建独立不可变快照。</summary>
        /// <param name = "reader">调用方拥有的JSON读取器。</param>
        /// <param name = "type">目标图像类型。</param>
        /// <param name = "existing">已有图像值，可为null。</param>
        /// <param name = "hasExisting">是否提供已有值。</param>
        /// <param name = "serializer">当前JSON序列化上下文。</param>
        public override ImageFrame ReadJson(
            JsonReader reader,
            Type type,
            ImageFrame? existing,
            bool hasExisting,
            JsonSerializer serializer
        )
        {
            return codec.Decode(Convert.FromBase64String((string)JObject.Load(reader)["png_base64"]!));
        }
    }
}
