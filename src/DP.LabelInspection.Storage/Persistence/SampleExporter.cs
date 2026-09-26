using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DP.LabelInspection.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Storage;

/// <summary>
/// 把已保存的检测任务导出为逐ROI样本集：每个ROI一张原图灰度裁图，外加一行索引（index.jsonl）记录
/// ROI类型、字符假设、算法判定与发现、逐字差异、缺陷框及人工判定。
/// 样本集同时用于传统算法改动的回放评估和训练式模型（方案B）的数据准备；只读原任务，不修改存储。
/// </summary>
public sealed class SampleExporter
{
    /// <summary>样本集清单的格式标识。</summary>
    public const string Schema = "dp.labelinspection.samples.v1";

    private readonly InspectionStore _store;

    /// <summary>从指定存储导出样本。</summary>
    /// <param name = "store">保存了完整任务的存储；导出只读取其中已完成的任务。</param>
    public SampleExporter(InspectionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 导出全部已完成任务的ROI样本到新目录。人工判定取该ROI最近一条ROI反馈；没有时，整任务人工判定为OK则视为该ROI为OK，
    /// 整任务为NG/REVIEW时无法确定是哪个ROI，标签留空但保留整任务判定。
    /// </summary>
    /// <param name = "destination">不存在的新目录；完成前写入临时目录，失败不留下半成品。</param>
    /// <param name = "margin">裁图在ROI四周额外保留的原图像素，0–256，默认8。</param>
    /// <returns>导出的样本数。</returns>
    public int Export(string destination, int margin = 8)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("Destination required.", nameof(destination));
        }

        if (margin < 0 || margin > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("Sample destination already exists; choose a new directory.");
        }

        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.Combine(pending, "crops"));
        try
        {
            int count = 0;
            var labelled = new Dictionary<string, int>(StringComparer.Ordinal);
            using (
                var index = new StreamWriter(
                    Path.Combine(pending, "index.jsonl"),
                    false,
                    new UTF8Encoding(false)
                )
            )
            {
                foreach (string job in _store.CompletedJobDirectories())
                {
                    foreach (var sample in Samples(job, margin))
                    {
                        string file = "crops/" + Path.GetFileName(job) + "-" + count.ToString("D4") + ".png";
                        File.WriteAllBytes(Path.Combine(pending, file), _store.Codec.EncodePng(sample.Crop));
                        sample.Line["file"] = file;
                        index.WriteLine(sample.Line.ToString(Formatting.None));
                        string label = (string?)sample.Line["label"] ?? "unlabelled";
                        labelled[label] = labelled.TryGetValue(label, out int n) ? n + 1 : 1;
                        count++;
                    }
                }
            }

            File.WriteAllText(
                Path.Combine(pending, "manifest.json"),
                new JObject
                {
                    { "schema", Schema },
                    { "utc", DateTimeOffset.UtcNow.ToString("O") },
                    { "samples", count },
                    { "margin", margin },
                    { "labels", JObject.FromObject(labelled) },
                }.ToString(),
                new UTF8Encoding(false)
            );
            Directory.Move(pending, destination);
            return count;
        }
        finally
        {
            if (Directory.Exists(pending))
            {
                Directory.Delete(pending, true);
            }
        }
    }

    private IEnumerable<(ImageFrame Crop, JObject Line)> Samples(string job, int margin)
    {
        var recipe = _store.DeserializeRecipe(File.ReadAllText(Path.Combine(job, "recipe.json")));
        var report = JObject.Parse(File.ReadAllText(Path.Combine(job, "report.json")));
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(job, "manifest.json")));
        var actual = _store.Codec.Decode(File.ReadAllBytes(Path.Combine(job, "actual.png")));
        var feedback = Feedback(job);
        string? jobHuman = feedback
            .Where(f => f["region"] == null)
            .Select(f => (string?)f["verdict"])
            .LastOrDefault();
        var analysis = report["analysis"] as JObject;
        int offsetX = (int?)Child(analysis, "offsetX") ?? 0,
            offsetY = (int?)Child(analysis, "offsetY") ?? 0;
        var results = (Child(analysis, "regions") as JArray ?? new JArray())
            .OfType<JObject>()
            .Where(r => r["regionName"] != null)
            .GroupBy(r => (string)r["regionName"]!)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var region in recipe.Regions)
        {
            if (!results.TryGetValue(region.Name, out var result))
            {
                continue;
            }

            int x0 = Math.Max(0, region.Bounds.X + offsetX - margin),
                y0 = Math.Max(0, region.Bounds.Y + offsetY - margin),
                x1 = Math.Min(actual.Width, region.Bounds.X + offsetX + region.Bounds.Width + margin),
                y1 = Math.Min(actual.Height, region.Bounds.Y + offsetY + region.Bounds.Height + margin);
            if (x1 - x0 < 1 || y1 - y0 < 1)
            {
                continue;
            }

            var crop = new PixelRect(x0, y0, x1 - x0, y1 - y0);
            var findings = (result["findings"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            string algorithm =
                findings.Any(f => Verdict(f["verdict"]) == "NG") ? "NG"
                : findings.Any(f => Verdict(f["verdict"]) == "REVIEW") ? "REVIEW"
                : "OK";
            string? regionHuman = feedback
                .Where(f => (string?)f["region"] == region.Name)
                .Select(f => (string?)f["verdict"])
                .LastOrDefault();
            string? label = regionHuman ?? (jobHuman == "OK" ? "OK" : null);
            var line = new JObject
            {
                { "job", Path.GetFileName(job) },
                { "utc", manifest["utc"] },
                { "recipe", recipe.Name },
                { "region", region.Name },
                { "kind", region.Kind.ToString() },
                { "bounds", new JArray(crop.X, crop.Y, crop.Width, crop.Height) },
                { "text", Child(result["recognition"], "text") },
                { "expected", region.Field.Expected },
                { "algorithm", algorithm },
                {
                    "codes",
                    new JArray(
                        findings
                            .Where(f => Verdict(f["verdict"]) != "OK")
                            .Select(f => (string?)f["code"])
                            .Where(c => c != null)
                            .Distinct()
                    )
                },
                {
                    "defects",
                    new JArray(
                        findings
                            .Where(f => Verdict(f["verdict"]) != "OK")
                            .Select(f => Box(f, crop))
                            .Where(b => b != null)
                    )
                },
                { "segmentation", Child(result["segmentation"], "basis") },
                {
                    "glyphs",
                    new JArray(
                        (result["glyphs"] as JArray ?? new JArray())
                            .OfType<JObject>()
                            .Select(g => Glyph(g, crop))
                    )
                },
                { "humanRegion", regionHuman },
                { "humanJob", jobHuman },
                { "label", label },
            };
            yield return (actual.Crop(crop), line);
        }
    }

    /// <summary>对象的子值；值为null、缺失或不是对象时返回null，而不是抛出异常。</summary>
    private static JToken? Child(JToken? token, string key)
    {
        return token is JObject o ? o[key] : null;
    }

    private static List<JObject> Feedback(string job)
    {
        string dir = Path.Combine(job, "feedback");
        if (!Directory.Exists(dir))
        {
            return new List<JObject>();
        }

        return Directory
            .GetFiles(dir, "*.json")
            .Select(f => JObject.Parse(File.ReadAllText(f)))
            .OrderBy(f => (string?)f["utc"], StringComparer.Ordinal)
            .ToList();
    }

    private static string Verdict(JToken? token)
    {
        // 报告中的判定枚举按数值保存（Ok=0、Review=1、Ng=2）；兼容字符串形式。
        if (token == null)
        {
            return "OK";
        }

        if (token.Type == JTokenType.Integer)
        {
            return (int)token switch
            {
                (int)EInspectionVerdict.Ng => "NG",
                (int)EInspectionVerdict.Review => "REVIEW",
                _ => "OK",
            };
        }

        return ((string?)token ?? "OK").ToUpperInvariant();
    }

    /// <summary>发现框转为裁图坐标；没有框或完全在裁图外时返回null。</summary>
    private static JObject? Box(JObject finding, PixelRect crop)
    {
        var rect = Rect(finding["bounds"], crop);
        return rect == null ? null : new JObject { { "code", finding["code"] }, { "box", rect } };
    }

    private static JObject Glyph(JObject glyph, PixelRect crop)
    {
        var comparison = glyph["comparison"] as JObject;
        return new JObject
        {
            { "character", Child(glyph["character"], "character") },
            { "status", glyph["status"] },
            { "box", Rect(Child(glyph["character"], "bounds"), crop) },
            { "difference", comparison?["difference"] },
            { "missing", comparison?["missing"] },
            { "extra", comparison?["extra"] },
        };
    }

    /// <summary>
    /// 原图矩形转为裁图坐标并裁剪到裁图内。报告中矩形有两种形式：[x,y,w,h]数组（非空矩形转换器），
    /// 以及可空矩形的{x,y,width,height}对象。
    /// </summary>
    private static JArray? Rect(JToken? token, PixelRect crop)
    {
        int[]? v =
            token is JArray a && a.Count == 4 && a.All(t => t.Type == JTokenType.Integer)
                ? a.Select(t => (int)t).ToArray()
            : token is JObject o
            && new[] { "x", "y", "width", "height" }.All(k => o[k]?.Type == JTokenType.Integer)
                ? new[] { (int)o["x"]!, (int)o["y"]!, (int)o["width"]!, (int)o["height"]! }
            : null;
        if (v == null)
        {
            return null;
        }

        int x = Math.Max(0, v[0] - crop.X),
            y = Math.Max(0, v[1] - crop.Y),
            right = Math.Min(crop.Width, v[0] + v[2] - crop.X),
            bottom = Math.Min(crop.Height, v[1] + v[3] - crop.Y);
        return right <= x || bottom <= y ? null : new JArray(x, y, right - x, bottom - y);
    }
}
