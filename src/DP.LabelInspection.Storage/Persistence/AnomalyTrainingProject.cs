using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Storage;

/// <summary>
/// 批量训练采集的保存与打开（dp.labelinspection.anomaly-training.v1）：记录图像文件路径、模型、样本框及逐字符样本的确认文本与取消的字符，
/// 大量采集可分多次完成。没有文件路径的图像（如当前图像）另存为PNG放在采集文件旁的同名文件夹中。
/// 逐字符样本打开后需重新提取（按保存的确认文本，不依赖OCR）。
/// </summary>
public static class AnomalyTrainingProject
{
    /// <summary>采集文件格式标识。</summary>
    public const string Schema = "dp.labelinspection.anomaly-training.v1";

    /// <summary>保存采集。</summary>
    /// <param name = "session">采集会话。</param>
    /// <param name = "path">采集文件路径（.json）。</param>
    /// <param name = "codec">用于另存没有路径的图像。</param>
    public static void Save(AnomalyTrainingSession session, string path, IImageCodec codec)
    {
        if (session == null || codec == null || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(session == null ? nameof(session) : nameof(path));
        }

        path = Path.GetFullPath(path);
        string folder = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + "-images"
        );
        var images = new JArray();
        for (int i = 0; i < session.Images.Count; i++)
        {
            var image = session.Images[i];
            string? file = image.Path;
            if (file == null || !File.Exists(file))
            {
                Directory.CreateDirectory(folder);
                file = Path.Combine(folder, i.ToString("D4") + ".png");
                File.WriteAllBytes(file, codec.EncodePng(image.Image));
            }

            images.Add(new JObject { { "name", image.Name }, { "path", file } });
        }

        var models = new JArray(
            session.Models.Select(m => new JObject
            {
                { "name", m.Name },
                { "kind", m.Kind.ToString() },
                { "region", m.Region?.Name },
                { "width", m.Width },
                { "height", m.Height },
                { "group", m.Kind == EAnomalyTrainingKind.Characters ? m.CharacterGroup : null },
            })
        );
        var samples = new JArray(
            session.Samples.Select(s =>
            {
                var o = new JObject
                {
                    { "image", session.Images.ToList().IndexOf(s.Image) },
                    { "model", s.Model.Name },
                    { "bounds", new JArray(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height) },
                };
                if (s.Model.Kind == EAnomalyTrainingKind.Characters)
                {
                    // 已提取的行保存核对后的身份作为确认文本，重新打开时按它分割，身份修改不会丢失。
                    string? text = s.Segmentation != null ? string.Concat(s.Labels) : s.ConfirmedText;
                    o["text"] = text;
                    o["excluded"] = new JArray(
                        Enumerable.Range(0, s.Include.Count).Where(i => !s.Include[i]).ToArray()
                    );
                }

                return o;
            })
        );
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(
            pending,
            new JObject
            {
                { "schema", Schema },
                { "snapRadius", session.SnapRadius },
                { "images", images },
                { "models", models },
                { "samples", samples },
            }.ToString(Formatting.Indented),
            new UTF8Encoding(false)
        );
        if (File.Exists(path))
        {
            File.Replace(pending, path, null);
        }
        else
        {
            File.Move(pending, path);
        }
    }

    /// <summary>打开采集；模型按名称重新关联当前配方的ROI（配方中已没有的只保留为独立模型）。</summary>
    /// <param name = "path">采集文件路径。</param>
    /// <param name = "codec">解码图像文件。</param>
    /// <param name = "regions">当前配方的ROI。</param>
    /// <param name = "excluded">
    /// 输出：各逐字符样本保存时取消的字符序号；重新提取后调用方可用<see cref = "AnomalyTrainingSession.SetInclude"/>恢复。
    /// </param>
    public static AnomalyTrainingSession Load(
        string path,
        IImageCodec codec,
        IEnumerable<InspectionRegion> regions,
        out IReadOnlyDictionary<AnomalyTrainingSample, int[]> excluded
    )
    {
        var doc = JObject.Parse(File.ReadAllText(path ?? throw new ArgumentNullException(nameof(path))));
        if ((string?)doc["schema"] != Schema)
        {
            throw new InvalidDataException("不是批量训练采集文件。");
        }

        var recipe = (regions ?? Array.Empty<InspectionRegion>()).ToDictionary(
            r => r.Name,
            StringComparer.Ordinal
        );
        var session = new AnomalyTrainingSession { SnapRadius = (int?)doc["snapRadius"] ?? 16 };
        var images = new List<AnomalyTrainingImage>();
        foreach (var i in (JArray)doc["images"]!)
        {
            string file = (string)i["path"]!;
            images.Add(session.AddImage(codec.Decode(File.ReadAllBytes(file)), (string)i["name"]!, file));
        }

        var models = new Dictionary<string, AnomalyTrainingModel>(StringComparer.Ordinal);
        foreach (var m in (JArray)doc["models"]!)
        {
            string name = (string)m["name"]!;
            var kind = (EAnomalyTrainingKind)Enum.Parse(typeof(EAnomalyTrainingKind), (string)m["kind"]!);
            string? regionName = (string?)m["region"];
            var region = regionName != null && recipe.TryGetValue(regionName, out var r) ? r : null;
            if (kind == EAnomalyTrainingKind.Characters && region != null && region.Kind != ERegionKind.Text)
            {
                region = null;
            }

            var model = session.AddModel(name, kind, region);
            if (
                kind == EAnomalyTrainingKind.FixedContent
                && (int?)m["width"] is int w
                && (int?)m["height"] is int h
            )
            {
                // 以保存的尺寸为准（可能在采集中改过），样本按原框恢复。
                session.SetSize(model, w, h);
            }

            if (kind == EAnomalyTrainingKind.Characters && (string?)m["group"] is string group)
            {
                session.SetGroup(model, group);
            }

            models[name] = model;
        }

        var skip = new Dictionary<AnomalyTrainingSample, int[]>();
        int snap = session.SnapRadius;
        session.SnapRadius = 0;
        try
        {
            foreach (var s in (JArray)doc["samples"]!)
            {
                var b = (JArray)s["bounds"]!;
                var sample = session.AddSample(
                    images[(int)s["image"]!],
                    models[(string)s["model"]!],
                    new PixelRect((int)b[0], (int)b[1], (int)b[2], (int)b[3])
                );
                if (sample.Model.Kind == EAnomalyTrainingKind.Characters)
                {
                    session.SetConfirmedText(sample, (string?)s["text"]);
                    skip[sample] =
                        ((JArray?)s["excluded"])?.Select(t => (int)t).ToArray() ?? Array.Empty<int>();
                }
            }
        }
        finally
        {
            session.SnapRadius = snap;
        }

        excluded = skip;
        return session;
    }
}
