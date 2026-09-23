using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>与UI无关的候选编辑和跨图暂存；加载新图清除当前编辑，不清除暂存参考。</summary>
public sealed class GlyphDraftSession
{
    private ImageFrame? _source;
    private string _hash = "",
        _sourceName = "";
    private long _next;
    private List<GlyphDraftCandidate> _candidates = new List<GlyphDraftCandidate>();
    private readonly List<List<GlyphDraftCandidate>> _undo = new List<List<GlyphDraftCandidate>>(),
        _redo = new List<List<GlyphDraftCandidate>>();
    private readonly List<GlyphImportItem> _pending = new List<GlyphImportItem>();
    private string? _pendingLibrary;

    /// <summary>当前源图，暂存项不会保留完整源图。</summary>
    public ImageFrame? Source => _source;

    /// <summary>当前图像候选，与调用方可变集合分离。</summary>
    public IReadOnlyList<GlyphDraftCandidate> Candidates => _candidates.AsReadOnly();

    /// <summary>等待明确发布的跨图独立图块。</summary>
    public IReadOnlyList<GlyphImportItem> Pending => _pending.AsReadOnly();

    /// <summary>待发布列表所属字库标识，空列表为null。</summary>
    public string? PendingLibraryId => _pendingLibrary;

    /// <summary>当前图像编辑是否可以撤销。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>当前图像编辑是否可以重做。</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>加载另一张不可变图像，不丢弃已暂存图块。</summary>
    /// <param name = "image">新的不可变源图，暂存项不保留整个源图。</param>
    /// <param name = "sourceName">简短来源名称，用于候选溯源，可为空。</param>
    public void LoadImage(ImageFrame image, string sourceName = "")
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        if (sourceName == null || sourceName.Length > 256)
        {
            throw new ArgumentException("Source name too long.");
        }

        string hash;
        using (var algorithm = SHA256.Create())
        {
            hash = BitConverter
                .ToString(algorithm.ComputeHash(image.CopyPixels()))
                .Replace("-", "")
                .ToLowerInvariant();
        }

        _source = image;
        _hash = hash;
        _sourceName = sourceName;
        ClearCandidates();
    }

    /// <summary>仅清除当前图像的候选和历史，不清除跨图暂存。</summary>
    public void ClearCandidates()
    {
        _candidates = new List<GlyphDraftCandidate>();
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>用提取快照替换候选；即使自动结果为零，仍允许手动添加。</summary>
    /// <param name = "segmentation">新物理分割快照，允许没有候选，不直接批准为参考。</param>
    public void ApplyExtraction(CharacterSegmentation segmentation)
    {
        if (segmentation == null)
        {
            throw new ArgumentNullException(nameof(segmentation));
        }

        RequireImage();
        var next = new List<GlyphDraftCandidate>();
        foreach (var patch in segmentation.Characters)
        {
            if (!patch.Bounds.Fits(_source!))
            {
                throw new ArgumentException("Candidate outside source image.");
            }

            if (patch.Patch.Width != patch.Bounds.Width || patch.Patch.Height != patch.Bounds.Height)
            {
                throw new ArgumentException("Patch dimensions disagree with source bounds.");
            }

            next.Add(
                new GlyphDraftCandidate(
                    Id(),
                    patch.Character,
                    patch.Bounds,
                    patch.Patch,
                    segmentation.Basis,
                    patch.Character
                )
            );
        }

        Commit(next);
    }

    /// <summary>添加明确绘制的裁图，即使OCR或自动分割没有产生候选。</summary>
    /// <param name = "bounds">用户明确绘制的原图整数范围。</param>
    /// <param name = "label">初始标签，可为空；无标签时不能暂存发布。</param>
    public string AddManual(PixelRect bounds, string label = "")
    {
        CheckBounds(bounds);
        CheckLabel(label);
        var value = new GlyphDraftCandidate(Id(), label, bounds, _source!.Crop(bounds), "manual_crop");
        Commit(_candidates.Concat(new[] { value }).ToList());
        return value.Id;
    }

    /// <summary>修改标签而不改变像素，无标签草稿不能暂存。</summary>
    /// <param name = "id">当前图像内的候选标识。</param>
    /// <param name = "label">人工编辑的独立标签，可为空，但空标签不能暂存。</param>
    public void SetLabel(string id, string label)
    {
        CheckLabel(label);
        var item = Get(id);
        if (item.Label == label)
        {
            return;
        }

        Replace(
            id,
            new GlyphDraftCandidate(
                id,
                label,
                item.Bounds,
                item.Image,
                item.Operation,
                item.ProvisionalCharacter
            )
        );
    }

    /// <summary>从原图重新裁取手动调整的矩形，需要重新人工复核。</summary>
    /// <param name = "id">要重新裁剪的候选标识。</param>
    /// <param name = "bounds">新的原图整数范围，重新裁剪后需复核。</param>
    public void Resize(string id, PixelRect bounds)
    {
        CheckBounds(bounds);
        var item = Get(id);
        if (bounds.Equals(item.Bounds))
        {
            return;
        }

        Replace(
            id,
            new GlyphDraftCandidate(
                id,
                item.Label,
                bounds,
                _source!.Crop(bounds),
                "manual_recrop",
                item.ProvisionalCharacter
            )
        );
    }

    /// <summary>按明确的原图X坐标切分独立图块；清空两侧标签，不擦除连接墨迹。</summary>
    /// <param name = "id">要切分的当前候选标识。</param>
    /// <param name = "originalX">原图X切分坐标，必须在候选内部并满足最小图块要求。</param>
    public void Split(string id, int originalX)
    {
        var item = Get(id);
        long offset = (long)originalX - item.Bounds.X;
        if (offset < 4 || item.Bounds.Width - offset < 4)
        {
            throw new ArgumentException("切线两侧至少各4个原始像素。");
        }

        int width = (int)offset;
        var left = new GlyphDraftCandidate(
            Id(),
            "",
            new PixelRect(item.Bounds.X, item.Bounds.Y, width, item.Bounds.Height),
            item.Image.Crop(new PixelRect(0, 0, width, item.Image.Height)),
            "manual_split"
        );
        var right = new GlyphDraftCandidate(
            Id(),
            "",
            new PixelRect(originalX, item.Bounds.Y, item.Bounds.Width - width, item.Bounds.Height),
            item.Image.Crop(new PixelRect(width, 0, item.Image.Width - width, item.Image.Height)),
            "manual_split"
        );
        var next = _candidates.ToList();
        int index = next.FindIndex(c => c.Id == id);
        next.RemoveAt(index);
        next.Insert(index, right);
        next.Insert(index, left);
        Commit(next);
    }

    /// <summary>删除当前候选；已暂存快照刻意保持独立。</summary>
    /// <param name = "id">要删除的当前候选标识，不影响独立暂存快照。</param>
    public void Remove(string id)
    {
        Get(id);
        Commit(_candidates.Where(c => c.Id != id).ToList());
    }

    /// <summary>撤销一次当前图像的裁剪、标签或切分编辑。</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(_candidates);
        _candidates = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>重做一次当前图像编辑。</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(_candidates);
        _candidates = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
    }

    /// <summary>暂存明确复核的选择；已有或待存重复标签会跳过并报告，不静默替换；无效项中止本次暂存。</summary>
    /// <param name = "libraryId">暂存列表所属的目标字库标识。</param>
    /// <param name = "ids">已明确复核的当前候选标识集合。</param>
    /// <param name = "existing">目标字库已有字符标签，用于重复检测。</param>
    /// <param name = "includeExisting">是否允许把已有标签也纳入待替换列表，仍需发布时明确同意替换。</param>
    public IReadOnlyList<string> Stage(
        string libraryId,
        IEnumerable<string> ids,
        IEnumerable<string> existing,
        bool includeExisting = false
    )
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            throw new ArgumentException("Select a library.");
        }

        if (_pendingLibrary != null && _pendingLibrary != libraryId)
        {
            throw new InvalidOperationException("待入库清单属于另一字库，请先保存或清空。");
        }

        if (ids == null || existing == null)
        {
            throw new ArgumentNullException();
        }

        var selected = ids.Distinct(StringComparer.Ordinal).Select(Get).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("请先勾选候选。");
        }

        var known = new HashSet<string>(existing, StringComparer.Ordinal);
        var queued = new HashSet<string>(_pending.Select(p => p.Character), StringComparer.Ordinal);
        var skipped = new List<string>();
        var added = new List<GlyphImportItem>();
        foreach (var candidate in selected)
        {
            if (candidate.Label.Length != 1)
            {
                throw new ArgumentException(
                    "所选字块还有空标签。请在“填写单字”列填入一个字母或数字，再核对暂存。"
                );
            }

            if ((!includeExisting && known.Contains(candidate.Label)) || !queued.Add(candidate.Label))
            {
                skipped.Add(candidate.Label);
                continue;
            }

            if (
                candidate.Image.Width < 4
                || candidate.Image.Height < 4
                || candidate.Image.Width > 512
                || candidate.Image.Height > 512
            )
            {
                throw new ArgumentException(
                    "字符 " + candidate.Label + " 的图块宽高须各为4–512像素。请调整边界后重试。"
                );
            }

            added.Add(new GlyphImportItem(candidate.Label, candidate.Image, "otsu", Provenance(candidate)));
        }

        if (_pending.Count + added.Count > 62)
        {
            throw new ArgumentException("每个字库最多暂存62个独立字母/数字。");
        }

        _pending.AddRange(added);
        if (_pending.Count > 0)
        {
            _pendingLibrary = libraryId;
        }

        return skipped.AsReadOnly();
    }

    /// <summary>移除一个暂存参考，不删除任何已保存版本。</summary>
    /// <param name = "character">要从暂存列表移除的大小写敏感字符标签。</param>
    public void RemovePending(string character)
    {
        _pending.RemoveAll(p => p.Character == character);
        if (_pending.Count == 0)
        {
            _pendingLibrary = null;
        }
    }

    /// <summary>明确丢弃尚未保存的跨图列表。</summary>
    public void ClearPending()
    {
        _pending.Clear();
        _pendingLibrary = null;
    }

    /// <summary>将暂存列表发布为一个版本，异常时保留全部暂存像素以便重试。</summary>
    /// <param name = "manager">宿主拥有的原子批量字库管理器。</param>
    /// <param name = "libraryId">与暂存列表一致的目标字库标识。</param>
    /// <param name = "expectedRevision">调用方基于的字库版本，过期则失败并保留暂存。</param>
    /// <param name = "replaceExisting">是否明确允许替换已有字符参考。</param>
    public int Publish(
        IGlyphBatchLibraryManager manager,
        string libraryId,
        int expectedRevision,
        bool replaceExisting = false
    )
    {
        if (manager == null)
        {
            throw new ArgumentNullException(nameof(manager));
        }

        if (_pending.Count == 0)
        {
            throw new InvalidOperationException("待入库清单为空。");
        }

        if (libraryId != _pendingLibrary)
        {
            throw new InvalidOperationException("待入库清单与目标字库不一致。");
        }

        int revision = manager.PutGlyphs(libraryId, expectedRevision, _pending.ToArray(), replaceExisting);
        ClearPending();
        return revision;
    }

    private string Id()
    {
        return "candidate-" + (++_next).ToString(CultureInfo.InvariantCulture);
    }

    private GlyphDraftCandidate Get(string id)
    {
        return _candidates.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException("请选择当前候选。");
    }

    private void Replace(string id, GlyphDraftCandidate value)
    {
        Commit(_candidates.Select(c => c.Id == id ? value : c).ToList());
    }

    private void Commit(List<GlyphDraftCandidate> next)
    {
        if (next.Count > 128)
        {
            throw new ArgumentException("当前图片候选最多128个。");
        }

        _undo.Add(_candidates);
        if (_undo.Count > 20)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        _candidates = next;
    }

    private void RequireImage()
    {
        if (_source == null)
        {
            throw new InvalidOperationException("请先导入图片。");
        }
    }

    private void CheckBounds(PixelRect bounds)
    {
        RequireImage();
        if (
            !bounds.Fits(_source!)
            || bounds.Width < 4
            || bounds.Height < 4
            || bounds.Width > 512
            || bounds.Height > 512
        )
        {
            throw new ArgumentException("手工字块须在原图内，宽高各4–512像素。请框选一个字或一对粘连字。");
        }
    }

    private static void CheckLabel(string label)
    {
        if (
            label == null
            || label.Length > 1
            || (label.Length == 1 && !FieldSettings.IsAlphanumeric(label[0]))
        )
        {
            throw new ArgumentException("标签需为空或一个A–Z/a–z/0–9字符。");
        }
    }

    private string Provenance(GlyphDraftCandidate candidate)
    {
        return "{\"source_pixel_sha256\":\""
            + _hash
            + "\",\"source_name\":"
            + Quote(_sourceName)
            + ",\"source_width\":"
            + _source!.Width
            + ",\"source_height\":"
            + _source.Height
            + ",\"patch_box\":["
            + candidate.Bounds.X
            + ","
            + candidate.Bounds.Y
            + ","
            + candidate.Bounds.Width
            + ","
            + candidate.Bounds.Height
            + "],\"operation\":"
            + Quote(candidate.Operation)
            + ",\"provisional_character\":"
            + Quote(candidate.ProvisionalCharacter)
            + ",\"review_required_split\":"
            + (
                candidate.Operation == "manual_split"
                || candidate.Operation == "thin_bridge_vertical_candidates"
                    ? "true"
                    : "false"
            )
            + ",\"human_reviewed\":true}";
    }

    private static string Quote(string value)
    {
        return "\""
            + string.Concat(
                value.Select(c =>
                    c == '"' ? "\\\""
                    : c == '\\' ? "\\\\"
                    : c < 32 ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)
                    : c.ToString()
                )
            )
            + "\"";
    }
}
