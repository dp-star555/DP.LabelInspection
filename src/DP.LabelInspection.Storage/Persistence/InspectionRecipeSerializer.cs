using System;
using System.IO;
using DP.LabelInspection.Contracts;
using Newtonsoft.Json;

namespace DP.LabelInspection.Storage;

/// <summary>不创建目录、不读写字库的原生配方序列化桥；完整保留ROI检测项目与固定库修订。</summary>
public sealed class InspectionRecipeSerializer
{
    private readonly JsonSerializerSettings _settings;
    /// <summary>使用与InspectionStore完全相同的构造校验和转换器。</summary>
    /// <param name="codec">配方像素扩展的编解码器，不由此对象拥有。</param>
    public InspectionRecipeSerializer(IImageCodec codec)
    {
        _settings = InspectionStore.CreateSerializationSettings(codec ?? throw new ArgumentNullException(nameof(codec)));
    }
    /// <summary>捕获可部署配方JSON；不改变字库ID/Revision。</summary>
    /// <param name="recipe">当前配方。</param>
    public string Serialize(InspectionRecipe recipe) => JsonConvert.SerializeObject(recipe ?? throw new ArgumentNullException(nameof(recipe)), _settings);
    /// <summary>加载至多1MB的原生配方，通过SDK构造函数验证。</summary>
    /// <param name="json">原生配方JSON。</param>
    public InspectionRecipe Deserialize(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (json.Length > 1024 * 1024) throw new ArgumentException("Recipe too large.", nameof(json));
        return JsonConvert.DeserializeObject<InspectionRecipe>(json, _settings) ?? throw new InvalidDataException("Empty recipe.");
    }
}

public sealed partial class InspectionStore
{
    internal static JsonSerializerSettings CreateSerializationSettings(IImageCodec codec)
    {
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MaxDepth = 64,
            ContractResolver = new RecipeResolver(), Formatting = Formatting.Indented,
        };
        settings.Converters.Add(new FrameConverter(codec));
        settings.Converters.Add(new RectConverter());
        return settings;
    }
}
