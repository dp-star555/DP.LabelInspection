using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DP.LabelInspection.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.Storage;

/// <summary>
/// 本地异常模型库（方法B），与字库的版本规则一致：每次修改发布新的不可变版本JSON，按调用方基于的版本拒绝过期编辑，
/// 归档不删除历史。模型字节按SHA256内容寻址单独保存（models/&lt;sha&gt;.bin），多个版本共享同一模型文件；
/// 导出文档内嵌模型字节，可在另一台机器导入为新库。
/// </summary>
public sealed class AnomalyLibraryStore : IAnomalyLibraryManager
{
    /// <summary>模型库文档格式标识。</summary>
    public const string Schema = "dp.labelinspection.anomaly-library.v1";

    private readonly string _root;

    /// <summary>在宿主指定根目录下的anomaly-libraries子目录保存模型库。</summary>
    /// <param name = "root">宿主选择的本地存储根目录，通常与<see cref = "InspectionStore"/>相同。</param>
    public AnomalyLibraryStore(string root)
    {
        _root = Path.Combine(
            Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))),
            "anomaly-libraries"
        );
    }

    /// <inheritdoc/>
    public AnomalyLibrarySnapshot LoadAnomalyLibrary(string id, int revision)
    {
        var doc = Read(id, revision);
        return new AnomalyLibrarySnapshot(
            id,
            revision,
            (string)doc["name"]!,
            ((JObject)doc["models"]!).Properties().Select(p => Entry(id, p.Name, (JObject)p.Value))
        );
    }

    /// <summary>返回最新已发布版本；不存在时为0，临时文件不算发布成功。</summary>
    /// <param name = "id">模型库标识。</param>
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

    /// <inheritdoc/>
    public IReadOnlyList<AnomalyLibraryInfo> ListAnomalyLibraries(bool includeArchived = false)
    {
        if (!Directory.Exists(_root))
        {
            return Array.Empty<AnomalyLibraryInfo>();
        }

        var list = new List<AnomalyLibraryInfo>();
        foreach (var path in Directory.GetDirectories(_root).OrderBy(p => p, StringComparer.Ordinal))
        {
            string id = Path.GetFileName(path);
            int revision = Latest(id);
            if (revision == 0)
            {
                continue;
            }

            var doc = Read(id, revision);
            bool archived = (bool?)doc["archived"] ?? false;
            if (includeArchived || !archived)
            {
                list.Add(
                    new AnomalyLibraryInfo(
                        id,
                        (string)doc["name"]!,
                        revision,
                        archived,
                        ((JObject)doc["models"]!).Count
                    )
                );
            }
        }

        return list.AsReadOnly();
    }

    /// <inheritdoc/>
    public string CreateAnomalyLibrary(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("Library name required.", nameof(name));
        }

        string id = "anomaly-" + Guid.NewGuid().ToString("N");
        Publish(
            new JObject
            {
                { "schema", Schema },
                { "id", id },
                { "revision", 1 },
                { "name", name },
                { "archived", false },
                { "models", new JObject() },
            },
            0
        );
        return id;
    }

    /// <inheritdoc/>
    public int PutAnomalyModel(
        string id,
        int expectedRevision,
        AnomalyModelEntry model,
        bool replaceExisting = false,
        string? provenanceJson = null
    )
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        return PutAnomalyModels(id, expectedRevision, new[] { model }, replaceExisting, provenanceJson);
    }

    /// <inheritdoc/>
    public int PutAnomalyModels(
        string id,
        int expectedRevision,
        IEnumerable<AnomalyModelEntry> models,
        bool replaceExisting = false,
        string? provenanceJson = null
    )
    {
        var batch = (models ?? throw new ArgumentNullException(nameof(models))).ToArray();
        if (
            batch.Length == 0
            || batch.Any(m => m == null)
            || batch.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() != batch.Length
        )
        {
            throw new ArgumentException("Provide uniquely keyed models.", nameof(models));
        }

        var doc = Read(id, expectedRevision);
        if ((bool?)doc["archived"] == true)
        {
            throw new InvalidOperationException("Archived library cannot accept models.");
        }

        var entries = (JObject)doc["models"]!;
        foreach (var model in batch)
        {
            var existing = entries[model.Key] as JObject;
            if (existing != null && !replaceExisting)
            {
                throw new InvalidOperationException(
                    "Model already exists: " + model.Key + ". Explicit replacement confirmation required."
                );
            }

            if (existing != null && ((int?)existing["scope"] ?? 0) != (int)model.Scope)
            {
                throw new InvalidOperationException(
                    "Key " + model.Key + " already holds a model of another scope; use a separate library."
                );
            }

            if (InspectionStore.Hash(model.CopyModel()) != model.Sha256)
            {
                throw new ArgumentException("Model hash mismatch: " + model.Key, nameof(models));
            }
        }

        var provenance = provenanceJson == null ? new JObject() : JObject.Parse(provenanceJson);
        foreach (var model in batch)
        {
            WriteBlob(id, model.CopyModel(), model.Sha256);
            var entry = Metadata(model);
            entry["provenance"] = provenance.DeepClone();
            entries[model.Key] = entry;
        }

        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <inheritdoc/>
    public int RemoveAnomalyModel(string id, int expectedRevision, string key)
    {
        var doc = Read(id, expectedRevision);
        if (key == null || !((JObject)doc["models"]!).Remove(key))
        {
            throw new ArgumentException("Model not present.", nameof(key));
        }

        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <inheritdoc/>
    public int ArchiveAnomalyLibrary(string id, int expectedRevision, bool archived = true)
    {
        var doc = Read(id, expectedRevision);
        doc["archived"] = archived;
        doc["revision"] = expectedRevision + 1;
        Publish(doc, expectedRevision);
        return expectedRevision + 1;
    }

    /// <inheritdoc/>
    public string ExportAnomalyLibrary(string id, int revision)
    {
        var doc = Read(id, revision);
        foreach (var p in ((JObject)doc["models"]!).Properties())
        {
            var entry = (JObject)p.Value;
            entry["model_base64"] = Convert.ToBase64String(ReadBlob(id, (string)entry["sha256"]!));
        }

        return doc.ToString(Formatting.Indented);
    }

    /// <inheritdoc/>
    public string ImportAnomalyLibrary(string json)
    {
        var doc = Parse(json);
        Validate(doc);
        string id = "anomaly-" + Guid.NewGuid().ToString("N");
        var models = (JObject)doc["models"]!;
        foreach (var p in models.Properties().ToArray())
        {
            var entry = (JObject)p.Value;
            string? encoded = (string?)entry["model_base64"];
            if (encoded == null)
            {
                throw new InvalidDataException("Imported model has no embedded bytes: " + p.Name);
            }

            var bytes = Convert.FromBase64String(encoded);
            if (InspectionStore.Hash(bytes) != (string?)entry["sha256"])
            {
                throw new InvalidDataException("Imported model hash mismatch: " + p.Name);
            }

            WriteBlob(id, bytes, (string)entry["sha256"]!);
            entry.Remove("model_base64");
        }

        doc["imported_from"] = new JObject
        {
            { "id", (string?)doc["id"] },
            { "revision", (int?)doc["revision"] },
            { "document_sha256", InspectionStore.Hash(Encoding.UTF8.GetBytes(json)) },
        };
        doc["id"] = id;
        doc["revision"] = 1;
        doc["archived"] = false;
        doc.Remove("created_at");
        Publish(doc, 0);
        return id;
    }

    /// <summary>读取精确版本的完整文档（不含模型字节），供报告快照及界面显示来源记录。</summary>
    /// <param name = "id">模型库标识。</param>
    /// <param name = "revision">精确版本号。</param>
    public JObject Read(string id, int revision)
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

    private AnomalyModelEntry Entry(string id, string key, JObject entry)
    {
        string sha = (string)entry["sha256"]!;
        var bytes = ReadBlob(id, sha);
        return new AnomalyModelEntry(
            key,
            bytes,
            sha,
            (string)entry["feature_source"]!,
            (int)entry["width"]!,
            (int)entry["height"]!,
            (int)entry["local_radius"]!,
            (int)entry["training_images"]!,
            (double)entry["threshold"]!,
            (int)entry["margin"]!,
            (int)entry["stride"]!,
            (int)entry["minimum_area"]!,
            (string?)entry["calibration"] ?? "",
            (EAnomalyModelScope)((int?)entry["scope"] ?? 0)
        );
    }

    private static JObject Metadata(AnomalyModelEntry model)
    {
        return new JObject
        {
            { "sha256", model.Sha256 },
            { "length", model.Length },
            { "feature_source", model.FeatureSource },
            { "width", model.Width },
            { "height", model.Height },
            { "local_radius", model.LocalRadius },
            { "training_images", model.TrainingImages },
            { "threshold", model.Threshold },
            { "margin", model.Margin },
            { "stride", model.Stride },
            { "minimum_area", model.MinimumArea },
            { "calibration", model.Calibration },
            { "scope", (int)model.Scope },
        };
    }

    private void Validate(JObject doc)
    {
        if (
            (string?)doc["schema"] != Schema
            || doc["revision"] == null
            || (int?)doc["revision"] < 1
            || string.IsNullOrWhiteSpace((string?)doc["name"])
            || !(doc["models"] is JObject models)
        )
        {
            throw new InvalidDataException("Unsupported anomaly library.");
        }

        _ = LibraryPath((string)doc["id"]!);
        foreach (var p in models.Properties())
        {
            if (
                !(p.Value is JObject e)
                || string.IsNullOrWhiteSpace(p.Name)
                || p.Name.Length > 100
                || !Regex.IsMatch(
                    (string?)e["sha256"] ?? "",
                    "^[0-9a-f]{64}$",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)
                )
                || new[]
                {
                    "feature_source",
                    "width",
                    "height",
                    "local_radius",
                    "training_images",
                    "threshold",
                    "margin",
                    "stride",
                    "minimum_area",
                }.Any(k => e[k] == null)
            )
            {
                throw new InvalidDataException("Invalid anomaly model entry: " + p.Name);
            }
        }
    }

    private void Publish(JObject doc, int expected)
    {
        string id = (string)doc["id"]!;
        Validate(doc);
        if (Latest(id) != expected || (int)doc["revision"]! != expected + 1)
        {
            throw new InvalidOperationException("Stale anomaly library edit. Reload before changing models.");
        }

        doc["updated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (doc["created_at"] == null)
        {
            doc["created_at"] = doc["updated_at"]!.DeepClone();
        }

        string dir = Path.Combine(LibraryPath(id), "revisions");
        Directory.CreateDirectory(dir);
        try
        {
            WriteNew(Path.Combine(dir, (expected + 1) + ".json"), Encoding.UTF8.GetBytes(doc.ToString()));
        }
        catch (IOException error) when (Latest(id) > expected)
        {
            throw new InvalidOperationException(
                "Stale anomaly library edit; another writer published first.",
                error
            );
        }
    }

    private void WriteBlob(string id, byte[] bytes, string sha)
    {
        string dir = Path.Combine(LibraryPath(id), "models");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, sha + ".bin");
        if (File.Exists(path) && InspectionStore.Hash(File.ReadAllBytes(path)) == sha)
        {
            return;
        }

        try
        {
            WriteNew(path, bytes);
        }
        catch (IOException) when (File.Exists(path) && InspectionStore.Hash(File.ReadAllBytes(path)) == sha)
        {
            // 另一写入者已保存相同内容的模型。
        }
    }

    private byte[] ReadBlob(string id, string sha)
    {
        var bytes = File.ReadAllBytes(Path.Combine(LibraryPath(id), "models", sha + ".bin"));
        if (InspectionStore.Hash(bytes) != sha)
        {
            throw new InvalidDataException("Stored anomaly model hash mismatch.");
        }

        return bytes;
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
            throw new ArgumentException("Invalid anomaly library id.");
        }

        return Path.Combine(_root, id);
    }

    private static JObject Parse(string json)
    {
        if (json == null || json.Length > 512 * 1024 * 1024)
        {
            throw new ArgumentException("Invalid anomaly library size.");
        }

        using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 64 };
        return JObject.Load(
            reader,
            new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }
        );
    }

    private static void WriteNew(string destination, byte[] bytes)
    {
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
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
