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
    private sealed class RectConverter : JsonConverter<PixelRect>
    {
        /// <summary>将原图整数矩形写入JSON。</summary>
        /// <param name = "writer">调用方拥有的JSON写入器。</param>
        /// <param name = "value">待写入的原图整数矩形。</param>
        /// <param name = "serializer">当前JSON序列化上下文。</param>
        public override void WriteJson(JsonWriter writer, PixelRect value, JsonSerializer serializer)
        {
            new JArray(value.X, value.Y, value.Width, value.Height).WriteTo(writer);
        }

        /// <summary>通过带校验的构造函数读取JSON矩形。</summary>
        /// <param name = "reader">调用方拥有的JSON读取器。</param>
        /// <param name = "type">目标矩形类型。</param>
        /// <param name = "existing">已有矩形值。</param>
        /// <param name = "hasExisting">是否提供已有值。</param>
        /// <param name = "serializer">当前JSON序列化上下文。</param>
        public override PixelRect ReadJson(
            JsonReader reader,
            Type type,
            PixelRect existing,
            bool hasExisting,
            JsonSerializer serializer
        )
        {
            var a = JArray.Load(reader);
            if (a.Count != 4 || a.Any(v => v.Type != JTokenType.Integer))
            {
                throw new InvalidDataException("Rectangle must have four integers.");
            }

            return new PixelRect((int)a[0], (int)a[1], (int)a[2], (int)a[3]);
        }
    }
}
