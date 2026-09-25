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

/// <summary>本地不可变字库版本、可移植配方、完整报告及只追加反馈，不依赖原生库或UI。</summary>
public sealed partial class InspectionStore : IGlyphLibraryManager, IGlyphBatchLibraryManager
{
    private readonly string _root;
    private readonly IImageCodec _codec;
    private readonly JsonSerializerSettings _json;

    /// <summary>在宿主指定根目录创建存储，使用注入的有界图像编解码器。</summary>
    /// <param name = "root">宿主选择的本地存储根目录。</param>
    /// <param name = "codec">宿主注入的有界图像编解码器，不引入UI依赖。</param>
    public InspectionStore(string root, IImageCodec codec)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        Directory.CreateDirectory(_root);
        _json = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 64,
            ContractResolver = new RecipeResolver(),
            Formatting = Formatting.Indented,
        };
        _json.Converters.Add(new FrameConverter(codec));
        _json.Converters.Add(new RectConverter());
    }

    /// <summary>返回精确不可变版本，不替换为当前最新版本。</summary>
    /// <param name = "id">字库类别标识。</param>
    /// <param name = "revision">要求的精确不可变版本号。</param>
    public GlyphLibrarySnapshot Load(string id, int revision)
    {
        var doc = ReadLibrary(id, revision);
        return Snapshot(doc);
    }

    /// <summary>读取完整可移植文档，包含未知的来源记录字段。</summary>
    /// <param name = "id">字库类别标识。</param>
    /// <param name = "revision">要求读取的精确版本号，保留未知来源字段。</param>
    public JObject ReadLibrary(string id, int revision)
    {
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        var doc = Parse(File.ReadAllText(Path.Combine(LibraryPath(id), "revisions", revision + ".json")));
        Validate(doc);
        if ((string?)doc["id"] != id || (int?)doc["revision"] != revision)
        {
            throw new InvalidDataException("Revision identity mismatch.");
        }

        return doc;
    }

    /// <summary>返回最新已发布版本，临时文件不算发布成功。</summary>
    /// <param name = "id">要查询最新已发布版本的类别标识。</param>
    public int Latest(string id)
    {
        string path = Path.Combine(LibraryPath(id), "revisions");
        if (!Directory.Exists(path))
        {
            return 0;
        }

        return Directory
            .GetFiles(path, "*.json")
            .Select(p => int.TryParse(Path.GetFileNameWithoutExtension(p), out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>列出类别最新版本信息，默认隐藏已归档类别。</summary>
    /// <param name = "includeArchived">是否包含已归档类别，默认不包含。</param>
    public IReadOnlyList<JObject> Libraries(bool includeArchived = false)
    {
        string root = Path.Combine(_root, "libraries");
        if (!Directory.Exists(root))
        {
            return Array.Empty<JObject>();
        }

        var list = new List<JObject>();
        foreach (var path in Directory.GetDirectories(root))
        {
            string id = Path.GetFileName(path);
            int revision = Latest(id);
            if (revision == 0)
            {
                continue;
            }

            var doc = ReadLibrary(id, revision);
            if (includeArchived || !((bool?)doc["archived"] ?? false))
            {
                list.Add(doc);
            }
        }

        return list.AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<GlyphLibraryInfo> ListLibraries(bool includeArchived = false)
    {
        return Libraries(includeArchived)
            .Select(d => new GlyphLibraryInfo(
                (string)d["id"]!,
                (string)d["name"]!,
                (int)d["revision"]!,
                (bool?)d["archived"] ?? false
            ))
            .ToArray();
    }

    /// <summary>使用新标识创建空类别。</summary>
    /// <param name = "name">新类别的显示名称。</param>
    public string CreateLibrary(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("Category name required.");
        }

        string id = "library-" + Guid.NewGuid().ToString("N");
        var doc = new JObject
        {
            { "schema", "labelscope.glyph-library.v1" },
            { "id", id },
            { "revision", 1 },
            { "name", name },
            { "archived", false },
            { "glyphs", new JObject() },
        };
        Publish(doc, 0);
        return id;
    }

    /// <summary>将可移植Python/C#格式导入为新类别，不静默修改配方绑定。</summary>
    /// <param name = "json">待导入的完整可移植字库JSON，不覆盖已有标识。</param>
    public string ImportLibrary(string json)
    {
        var doc = Parse(json);
        Validate(doc);
        doc["imported_from"] = new JObject
        {
            { "id", (string?)doc["id"] },
            { "revision", (int?)doc["revision"] },
            { "document_sha256", Hash(Encoding.UTF8.GetBytes(json)) },
        };
        string id = "library-" + Guid.NewGuid().ToString("N");
        doc["id"] = id;
        doc["revision"] = 1;
        doc["archived"] = false;
        Publish(doc, 0);
        return id;
    }

    /// <summary>仅在不存在任何版本时安装可信初始库，不重置用户已有编辑。</summary>
    /// <param name = "json">可信初始库的完整JSON，仅在该库尚无版本时安装。</param>
    public void InstallSeed(string json)
    {
        var doc = Parse(json);
        Validate(doc);
        if ((int)doc["revision"]! != 1)
        {
            throw new ArgumentException("Seed must be revision 1.");
        }

        if (Latest((string)doc["id"]!) == 0)
        {
            Publish(doc, 0);
        }
    }

    /// <summary>通过新版本替换一个独立标签参考，拒绝过期编辑。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "character">大小写敏感的独立字符标签。</param>
    /// <param name = "image">独立不可变参考图块。</param>
    /// <param name = "binarization">二值化模式：otsu自动阈值、fixed固定阈值或midpoint墨色/纸色中点阈值。</param>
    /// <param name = "provenanceJson">可选来源证据JSON。</param>
    public int PutGlyph(
        string id,
        int expectedRevision,
        string character,
        ImageFrame image,
        string binarization = "otsu",
        string? provenanceJson = null
    )
    {
        var png = _codec.EncodePng(image);
        _ = new GlyphReference(character, image, Hash(png), binarization);
        var doc = ReadLibrary(id, expectedRevision);
        ((JObject)doc["glyphs"]!)[character] = new JObject
        {
            { "png_base64", Convert.ToBase64String(png) },
            { "sha256", Hash(png) },
            { "width", image.Width },
            { "height", image.Height },
            { "source", "user_candidate" },
            { "binarization", binarization },
            { "provenance", provenanceJson == null ? new JObject() : JObject.Parse(provenanceJson) },
        };
        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <inheritdoc/>
    public int PutGlyphs(
        string id,
        int expectedRevision,
        IEnumerable<GlyphImportItem> items,
        bool replaceExisting = false
    )
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var copy = items.Take(63).ToArray();
        if (
            copy.Length == 0
            || copy.Length > 62
            || copy.Any(i => i == null)
            || copy.Select(i => i.Character).Distinct(StringComparer.Ordinal).Count() != copy.Length
        )
        {
            throw new ArgumentException(
                "Select 1–62 uniquely labeled references; choose one sample per character."
            );
        }

        var doc = ReadLibrary(id, expectedRevision);
        if ((bool?)doc["archived"] == true)
        {
            throw new InvalidOperationException("Archived library cannot accept quick imports.");
        }

        var glyphs = (JObject)doc["glyphs"]!;
        foreach (var item in copy)
        {
            if (!replaceExisting && glyphs[item.Character] != null)
            {
                throw new InvalidOperationException(
                    "Character already exists: "
                        + item.Character
                        + ". Explicit replacement confirmation required."
                );
            }

            var png = _codec.EncodePng(item.Image);
            glyphs[item.Character] = new JObject
            {
                { "png_base64", Convert.ToBase64String(png) },
                { "sha256", Hash(png) },
                { "width", item.Image.Width },
                { "height", item.Image.Height },
                { "source", "user_reviewed_batch" },
                { "binarization", item.Binarization },
                {
                    "provenance",
                    item.ProvenanceJson == null ? new JObject() : JObject.Parse(item.ProvenanceJson)
                },
            };
        }

        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <inheritdoc/>
    public int PutSheet(
        string id,
        int expectedRevision,
        ImageFrame sheet,
        string alphabet,
        int rows,
        int columns,
        int padding = 1,
        string binarization = "otsu"
    )
    {
        if (
            sheet == null
            || string.IsNullOrEmpty(alphabet)
            || alphabet.Length > 62
            || alphabet.Distinct().Count() != alphabet.Length
            || alphabet.Any(c => !FieldSettings.IsAlphanumeric(c))
            || rows < 1
            || columns < 1
            || (long)rows * columns != alphabet.Length
            || padding < 0
            || padding > 32
        )
        {
            throw new ArgumentException("Invalid regular sheet declaration.");
        }

        var doc = ReadLibrary(id, expectedRevision);
        string sheetHash = Hash(_codec.EncodePng(sheet));
        for (int i = 0; i < alphabet.Length; i++)
        {
            int row = i / columns,
                col = i % columns,
                left = col * sheet.Width / columns + padding,
                top = row * sheet.Height / rows + padding;
            var box = new PixelRect(
                left,
                top,
                (col + 1) * sheet.Width / columns - padding - left,
                (row + 1) * sheet.Height / rows - padding - top
            );
            var image = sheet.Crop(box);
            var png = _codec.EncodePng(image);
            var glyph = new GlyphReference(alphabet[i].ToString(), image, Hash(png), binarization);
            ((JObject)doc["glyphs"]!)[glyph.Character] = new JObject
            {
                { "png_base64", Convert.ToBase64String(png) },
                { "sha256", glyph.Sha256 },
                { "width", image.Width },
                { "height", image.Height },
                { "binarization", binarization },
                { "source", "user_declared_sheet_cell" },
                {
                    "provenance",
                    new JObject
                    {
                        { "decoded_sheet_sha256", sheetHash },
                        { "source_box", new JArray(box.X, box.Y, box.Width, box.Height) },
                        { "selection", "explicit grid input; stored as independent character" },
                    }
                },
            };
        }

        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <summary>通过新不可变版本移除一个标签。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "character">要移除的大小写敏感字符标签。</param>
    public int RemoveGlyph(string id, int expectedRevision, string character)
    {
        var doc = ReadLibrary(id, expectedRevision);
        if (!((JObject)doc["glyphs"]!).Remove(character))
        {
            throw new ArgumentException("Character not present.");
        }

        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <summary>通过发布新版本归档或取消归档，历史像素仍可读取。</summary>
    /// <param name = "id">目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的版本，过期修改会被拒绝。</param>
    /// <param name = "archived">true归档，false取消归档，均通过发布新版本实现。</param>
    public int Archive(string id, int expectedRevision, bool archived = true)
    {
        var doc = ReadLibrary(id, expectedRevision);
        doc["archived"] = archived;
        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <summary>导出完整可移植类别版本，保留原PNG字节及来源记录。</summary>
    /// <param name = "id">要导出的类别标识。</param>
    /// <param name = "revision">固定的导出版本号。</param>
    public string ExportLibrary(string id, int revision)
    {
        return ReadLibrary(id, revision).ToString(Formatting.Indented);
    }

    /// <summary>序列化固定配方，图像和参考在报告中另行保存。</summary>
    /// <param name = "recipe">需要序列化的固定配方，不隐式升级字库引用。</param>
    public string SerializeRecipe(InspectionRecipe recipe)
    {
        return JsonConvert.SerializeObject(recipe, _json);
    }

    /// <summary>通过带校验的构造函数加载原生DP配方。</summary>
    /// <param name = "json">待解析的原生DP配方JSON，通过构造校验加载。</param>
    public InspectionRecipe DeserializeRecipe(string json)
    {
        if (json.Length > 1024 * 1024)
        {
            throw new ArgumentException("Recipe too large.");
        }

        return JsonConvert.DeserializeObject<InspectionRecipe>(json, _json)
            ?? throw new InvalidDataException("Empty recipe.");
    }

    /// <summary>以事务方式保存完整任务，包含精确配方、参考快照、像素及结果。</summary>
    /// <param name = "request">本次不可变请求，包含原图、配方及参考。</param>
    /// <param name = "report">对应的完整检测报告。</param>
    /// <param name = "annotated">可选标注图快照，不替代原图保存。</param>
    public string SaveReport(InspectionRequest request, InspectionReport report, ImageFrame? annotated = null)
    {
        string id = Guid.NewGuid().ToString("N"),
            jobs = Path.Combine(_root, "jobs"),
            temp = Path.Combine(jobs, ".pending-" + id);
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(
                Path.Combine(temp, "recipe.json"),
                SerializeRecipe(request.Recipe),
                Encoding.UTF8
            );
            File.WriteAllText(
                Path.Combine(temp, "task-context.json"),
                JsonConvert.SerializeObject(new { request.CycleId, request.TaskData }, _json),
                Encoding.UTF8
            );
            File.WriteAllText(
                Path.Combine(temp, "report.json"),
                JsonConvert.SerializeObject(report, _json),
                Encoding.UTF8
            );
            File.WriteAllBytes(Path.Combine(temp, "actual.png"), _codec.EncodePng(request.Actual));
            if (request.Reference != null)
            {
                File.WriteAllBytes(Path.Combine(temp, "reference.png"), _codec.EncodePng(request.Reference));
            }

            if (annotated != null)
            {
                File.WriteAllBytes(Path.Combine(temp, "annotated.png"), _codec.EncodePng(annotated));
            }

            var snapshots = new JObject();
            foreach (var region in request.Recipe.Regions.Where(r => r.Field.LibraryId != null))
            {
                string key = region.Field.LibraryId + "@" + region.Field.LibraryRevision;
                if (snapshots[key] != null)
                {
                    continue;
                }

                try
                {
                    snapshots[key] = ReadLibrary(
                        region.Field.LibraryId!,
                        region.Field.LibraryRevision!.Value
                    );
                }
                catch (FileNotFoundException)
                {
                    snapshots[key] = new JObject { { "status", "missing" } };
                }
                catch (DirectoryNotFoundException)
                {
                    snapshots[key] = new JObject { { "status", "missing" } };
                }
            }

            foreach (
                var region in report.Analysis.Regions.Where(r => r.Glyphs.Any(g => g.ReferenceSha256 != null))
            )
            {
                var source = request.Recipe.Regions.SingleOrDefault(r => r.Name == region.RegionName);
                if (source?.Field.LibraryId == null)
                {
                    throw new InvalidDataException("Compared glyphs have no recipe binding.");
                }

                var snapshot = snapshots[source.Field.LibraryId + "@" + source.Field.LibraryRevision];
                foreach (var glyph in region.Glyphs.Where(g => g.ReferenceSha256 != null))
                {
                    if (
                        (string?)snapshot?["glyphs"]?[glyph.Character.Character]?["sha256"]
                        != glyph.ReferenceSha256
                    )
                    {
                        throw new InvalidDataException(
                            "Report reference snapshot differs from inspected pixels."
                        );
                    }
                }
            }

            File.WriteAllText(Path.Combine(temp, "libraries.json"), snapshots.ToString(), Encoding.UTF8);
            var hashes = new JObject();
            foreach (var file in Directory.GetFiles(temp))
            {
                hashes[Path.GetFileName(file)] = Hash(File.ReadAllBytes(file));
            }

            File.WriteAllText(
                Path.Combine(temp, "manifest.json"),
                new JObject
                {
                    { "schema", "dp.labelinspection.report.v1" },
                    { "id", id },
                    { "utc", DateTimeOffset.UtcNow.ToString("O") },
                    { "verdict", report.Verdict.ToString() },
                    { "hashes", hashes },
                }.ToString(),
                Encoding.UTF8
            );
            Directory.Move(temp, Path.Combine(jobs, id));
            return id;
        }
        catch
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }

            throw;
        }
    }

    /// <summary>列出完整历史，排除未完成任务。</summary>
    public IReadOnlyList<JObject> History()
    {
        string root = Path.Combine(_root, "jobs");
        if (!Directory.Exists(root))
        {
            return Array.Empty<JObject>();
        }

        return Directory
            .GetDirectories(root)
            .Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))
            .Select(p => JObject.Parse(File.ReadAllText(Path.Combine(p, "manifest.json"))))
            .OrderByDescending(j => (string?)j["utc"])
            .ToArray();
    }

    /// <summary>追加人工反馈，不替换算法判定。</summary>
    /// <param name = "jobId">已完成任务标识。</param>
    /// <param name = "verdict">人工判定，只接受OK、NG或REVIEW。</param>
    /// <param name = "comment">人工说明，最多4000字符。</param>
    /// <param name = "author">反馈作者标识，最多200字符。</param>
    public void AddFeedback(string jobId, string verdict, string comment, string author)
    {
        if (verdict != "OK" && verdict != "NG" && verdict != "REVIEW")
        {
            throw new ArgumentException("Invalid human verdict.");
        }

        if (comment.Length > 4000 || author.Length > 200)
        {
            throw new ArgumentException("Feedback too long.");
        }

        string root = JobPath(jobId);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException();
        }

        string dir = Path.Combine(root, "feedback");
        Directory.CreateDirectory(dir);
        WriteNew(
            Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"),
            new JObject
            {
                { "utc", DateTimeOffset.UtcNow.ToString("O") },
                { "verdict", verdict },
                { "comment", comment },
                { "author", author },
            }.ToString()
        );
    }

    /// <summary>将已完成任务的不可变快照导出到新ZIP文件。</summary>
    /// <param name = "jobId">已完成任务标识，须存在完整清单。</param>
    /// <param name = "destination">新ZIP的目标路径，不覆盖已有导出文件。</param>
    public void ExportReport(string jobId, string destination)
    {
        string root = JobPath(jobId);
        if (!File.Exists(Path.Combine(root, "manifest.json")))
        {
            throw new FileNotFoundException("Completed job not found.");
        }

        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        foreach (var expected in ((JObject)manifest["hashes"]!).Properties())
        {
            if (
                Path.GetFileName(expected.Name) != expected.Name
                || expected.Name.Contains("\\")
                || expected.Name.Contains("/")
            )
            {
                throw new InvalidDataException("Unsafe report manifest path.");
            }

            if (Hash(File.ReadAllBytes(Path.Combine(root, expected.Name))) != (string?)expected.Value)
            {
                throw new InvalidDataException("Stored report hash mismatch: " + expected.Name);
            }
        }

        if (File.Exists(destination))
        {
            throw new IOException("Export destination already exists; choose a new file.");
        }

        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry(
                        file.Substring(root.Length + 1).Replace('\\', '/'),
                        CompressionLevel.Optimal
                    );
                    using var source = File.OpenRead(file);
                    using var output = entry.Open();
                    source.CopyTo(output);
                }
            }

            File.Move(pending, destination);
        }
        finally
        {
            if (File.Exists(pending))
            {
                File.Delete(pending);
            }
        }
    }

    /// <summary>计算精确编码像素或文档字节的SHA256。</summary>
    /// <param name = "bytes">需要计算哈希的精确原始字节，不重新编码。</param>
    public static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private string LibraryPath(string id)
    {
        if (
            id == null
            || !Regex.IsMatch(
                id,
                "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            )
        )
        {
            throw new ArgumentException("Invalid category id.");
        }

        return Path.Combine(_root, "libraries", id);
    }

    private string JobPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Invalid job id.");
        }

        return Path.Combine(_root, "jobs", id);
    }

    private static JObject Parse(string json)
    {
        if (json == null || json.Length > 64 * 1024 * 1024)
        {
            throw new ArgumentException("Invalid library size.");
        }

        using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 64 };
        return JObject.Load(
            reader,
            new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }
        );
    }

    private void Validate(JObject doc)
    {
        if (
            (string?)doc["schema"] != "labelscope.glyph-library.v1"
            || (int?)doc["revision"] < 1
            || doc["revision"] == null
            || string.IsNullOrWhiteSpace((string?)doc["name"])
        )
        {
            throw new InvalidDataException("Unsupported glyph library.");
        }

        _ = LibraryPath((string)doc["id"]!);
        _ = Snapshot(doc);
    }

    private GlyphLibrarySnapshot Snapshot(JObject doc)
    {
        var entries = doc["glyphs"] as JObject ?? throw new InvalidDataException("Missing glyph dictionary.");
        if (entries.Count > 62)
        {
            throw new InvalidDataException("At most 62 independent alphanumeric labels.");
        }

        var glyphs = new List<GlyphReference>();
        foreach (var p in entries.Properties())
        {
            var entry = (JObject)p.Value;
            string encoded = (string)entry["png_base64"]!;
            if (encoded == null || encoded.Length > 3 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid reference PNG.");
            }

            var png = Convert.FromBase64String(encoded);
            if (
                png.Length < 24
                || png[0] != 137
                || png[1] != 80
                || png[2] != 78
                || png[3] != 71
                || png[4] != 13
                || png[5] != 10
                || png[6] != 26
                || png[7] != 10
            )
            {
                throw new InvalidDataException("Reference must contain PNG bytes.");
            }

            string hash = Hash(png);
            if (hash != (string?)entry["sha256"])
            {
                throw new InvalidDataException("Reference hash mismatch.");
            }

            var image = _codec.Decode(png);
            if (image.Width != (int?)entry["width"] || image.Height != (int?)entry["height"])
            {
                throw new InvalidDataException("Reference dimensions mismatch.");
            }

            glyphs.Add(new GlyphReference(p.Name, image, hash, (string?)entry["binarization"] ?? "fixed"));
        }

        return new GlyphLibrarySnapshot(
            (string)doc["id"]!,
            (int)doc["revision"]!,
            (string)doc["name"]!,
            glyphs
        );
    }

    private void Publish(JObject doc, int expected)
    {
        string id = (string)doc["id"]!;
        Validate(doc);
        if (Latest(id) != expected || (int)doc["revision"]! != expected + 1)
        {
            throw new InvalidOperationException("Stale library edit. Reload before changing references.");
        }

        doc["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        if (doc["created_at"] == null)
        {
            doc["created_at"] = doc["updated_at"]!.DeepClone();
        }

        string dir = Path.Combine(LibraryPath(id), "revisions");
        Directory.CreateDirectory(dir);
        try
        {
            WriteNew(Path.Combine(dir, (expected + 1) + ".json"), doc.ToString());
        }
        catch (IOException error) when (Latest(id) > expected)
        {
            throw new InvalidOperationException("Stale library edit; another writer published first.", error);
        }
    }

    private static void WriteNew(string destination, string text)
    {
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            File.Move(temp, destination);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
