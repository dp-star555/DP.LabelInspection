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
    private PixelSnapshot? _source;
    private string _hash = "",
        _sourceName = "";
    private long _next;
    private List<GlyphDraftCandidate> _candidates = new List<GlyphDraftCandidate>();
    private List<GlyphDraftRegion> _regions = new List<GlyphDraftRegion>();
    private readonly List<DraftState> _undo = new List<DraftState>(),
        _redo = new List<DraftState>();
    private long _regionSequence;

    /// <summary>同一来源图最多32个单行制作ROI；每行仍受原有识别/分割预算约束。</summary>
    public const int MaximumRegions = 32;

    private sealed class DraftState
    {
        internal DraftState(List<GlyphDraftCandidate> candidates, List<GlyphDraftRegion> regions)
        {
            Candidates = candidates;
            Regions = regions;
        }

        internal List<GlyphDraftCandidate> Candidates { get; }
        internal List<GlyphDraftRegion> Regions { get; }
    }

    private readonly List<GlyphImportItem> _pending = new List<GlyphImportItem>();
    private string? _pendingLibrary;

    /// <summary>当前源图，暂存项不会保留完整源图。</summary>
    public PixelSnapshot? Source => _source;

    /// <summary>当前图像候选，与调用方可变集合分离。</summary>
    public IReadOnlyList<GlyphDraftCandidate> Candidates => _candidates.AsReadOnly();

    /// <summary>当前图独立单行ROI，各自保留人工文字、提取状态和候选归属。</summary>
    public IReadOnlyList<GlyphDraftRegion> Regions => _regions.AsReadOnly();

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
    public void LoadImage(PixelSnapshot image, string sourceName = "")
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
        _regionSequence = 0;
        ClearCandidates();
    }

    /// <summary>清除当前图像的区域、候选和历史，不清除跨图暂存。</summary>
    public void ClearCandidates()
    {
        _candidates = new List<GlyphDraftCandidate>();
        _regions = new List<GlyphDraftRegion>();
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>添加一条水平单行制作ROI；相同范围选回原区域，不重复注册。</summary>
    /// <param name="bounds">当前来源图中的原图整数范围。</param>
    /// <returns>当前图内有稳定标识的区域。</returns>
    public GlyphDraftRegion AddRegion(PixelRect bounds)
    {
        CheckRegionBounds(bounds);
        var existing = _regions.FirstOrDefault(r => r.Bounds.Equals(bounds));
        if (existing != null)
            return existing;
        if (_regions.Count >= MaximumRegions)
            throw new ArgumentException("每张图最多32个文字行ROI。");
        var region = new GlyphDraftRegion(Guid.NewGuid().ToString("N"), "ROI " + (++_regionSequence), bounds);
        Commit(_candidates, _regions.Concat(new[] { region }).ToList());
        return region;
    }

    /// <summary>保存指定区域的人工文字，null或空字符串表示批量提取时使用OCR；不改候选身份。</summary>
    /// <param name="id">当前图内区域标识。</param>
    /// <param name="text">人工单行文字草稿，最多256个UTF-16代码单元，正式提取时再校验。</param>
    public void SetRegionText(string id, string? text)
    {
        if (text?.Length > 256)
            throw new ArgumentException("单行人工文字最多256个UTF-16代码单元。");
        var region = Region(id);
        text = string.IsNullOrEmpty(text) ? null : text;
        if (region.ConfirmedText == text)
            return;
        ReplaceRegion(
            new GlyphDraftRegion(region.Id, region.Name, region.Bounds, text, region.Extraction, region.Error)
        );
    }

    /// <summary>调整一个ROI，仅清除该ROI旧候选/提取证据，已暂存快照和其他ROI不变；可撤销。</summary>
    /// <param name="id">要调整的制作区域。</param>
    /// <param name="bounds">新的水平单行范围。</param>
    public void SetRegionBounds(string id, PixelRect bounds)
    {
        CheckRegionBounds(bounds);
        var region = Region(id);
        if (region.Bounds.Equals(bounds))
            return;
        var updated = new GlyphDraftRegion(id, region.Name, bounds, region.ConfirmedText);
        Commit(
            _candidates.Where(c => c.RegionId != id).ToList(),
            _regions.Select(r => r.Id == id ? updated : r).ToList()
        );
    }

    /// <summary>删除一个ROI及其候选；重叠的其他区域和已暂存快照不受影响。</summary>
    /// <param name="id">当前图内区域标识。</param>
    public void RemoveRegion(string id)
    {
        _ = Region(id);
        Commit(_candidates.Where(c => c.RegionId != id).ToList(), _regions.Where(r => r.Id != id).ToList());
    }

    /// <summary>清除当前图所有ROI及其候选，保留独立手工裁图与跨图暂存，可撤销。</summary>
    public void ClearRegions() =>
        Commit(_candidates.Where(c => c.RegionId == null).ToList(), new List<GlyphDraftRegion>());

    /// <summary>成功提取仅替换所属ROI候选，不重复追加；零候选结果保留旧候选并记录失败，不影响其他ROI。</summary>
    /// <param name="id">目标制作区域标识。</param>
    /// <param name="extraction">独立像素和未改写的原始提取证据。</param>
    public void ApplyRegionExtraction(string id, GlyphCandidateExtraction extraction)
    {
        if (extraction == null)
            throw new ArgumentNullException(nameof(extraction));
        var region = Region(id);
        if (extraction.Segmentation.Characters.Count == 0)
        {
            ReplaceRegion(
                new GlyphDraftRegion(
                    id,
                    region.Name,
                    region.Bounds,
                    region.ConfirmedText,
                    extraction,
                    extraction.Segmentation.Reason
                )
            );
            return;
        }
        foreach (var patch in extraction.Segmentation.Characters)
            if (!Within(patch.Bounds, region.Bounds))
                throw new ArgumentException("候选不属于指定ROI。");
        var added = BuildCandidates(extraction.Segmentation, id);
        int index = _candidates.FindIndex(c => c.RegionId == id);
        var next = _candidates.Where(c => c.RegionId != id).ToList();
        next.InsertRange(index < 0 ? next.Count : index, added);
        var updated = new GlyphDraftRegion(
            id,
            region.Name,
            region.Bounds,
            extraction.ConfirmedText ?? region.ConfirmedText,
            extraction
        );
        Commit(next, _regions.Select(r => r.Id == id ? updated : r).ToList());
    }

    /// <summary>记录异常或取消原因但保留该区域旧候选和其他区域结果。</summary>
    /// <param name="id">提取失败的制作区域标识。</param>
    /// <param name="error">失败说明，最多保留1024个代码单元。</param>
    public void RecordRegionFailure(string id, string error)
    {
        if (error == null)
            throw new ArgumentNullException(nameof(error));
        var region = Region(id);
        ReplaceRegion(
            new GlyphDraftRegion(
                id,
                region.Name,
                region.Bounds,
                region.ConfirmedText,
                region.Extraction,
                error.Length > 1024 ? error.Substring(0, 1024) : error
            )
        );
    }

    /// <summary>兼容整表替换入口，同时清除制作ROI；多ROI调用者应使用ApplyRegionExtraction隔离各区域。</summary>
    /// <param name = "segmentation">新物理分割快照，允许没有候选，不直接批准为参考。</param>
    public void ApplyExtraction(CharacterSegmentation segmentation)
    {
        if (segmentation == null)
        {
            throw new ArgumentNullException(nameof(segmentation));
        }

        Commit(BuildCandidates(segmentation, null), new List<GlyphDraftRegion>());
    }

    private List<GlyphDraftCandidate> BuildCandidates(CharacterSegmentation segmentation, string? regionId)
    {
        RequireImage();
        if (segmentation.Characters.Count > 128)
            throw new ArgumentException("每个单行ROI最多128个候选。");
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
                    patch.Character,
                    regionId
                )
            );
        }

        return next;
    }

    /// <summary>添加明确绘制的裁图，即使OCR或自动分割没有产生候选。</summary>
    /// <param name = "bounds">用户明确绘制的原图整数范围。</param>
    /// <param name = "label">初始标签，可为空；无标签时不能暂存发布。</param>
    public string AddManual(PixelRect bounds, string label = "") => AddManual(bounds, label, null);

    /// <summary>添加显式归属制作ROI的手工裁图，保留原双参数入口的兼容性。</summary>
    /// <param name="bounds">原图整数裁图范围。</param>
    /// <param name="label">独立标签，可为空。</param>
    /// <param name="regionId">显式所属ROI；null为独立裁图，不通过几何相交猜归属。</param>
    public string AddManual(PixelRect bounds, string label, string? regionId)
    {
        CheckBounds(bounds);
        CheckLabel(label);
        if (regionId != null && !Within(bounds, Region(regionId).Bounds))
            throw new ArgumentException("手工候选超出所属ROI。");
        var value = new GlyphDraftCandidate(
            Id(),
            label,
            bounds,
            _source!.Crop(bounds),
            "manual_crop",
            regionId: regionId
        );
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
                item.ProvisionalCharacter,
                item.RegionId
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
                item.ProvisionalCharacter,
                item.RegionId
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
            "manual_split",
            regionId: item.RegionId
        );
        var right = new GlyphDraftCandidate(
            Id(),
            "",
            new PixelRect(originalX, item.Bounds.Y, item.Bounds.Width - width, item.Bounds.Height),
            item.Image.Crop(new PixelRect(width, 0, item.Image.Width - width, item.Image.Height)),
            "manual_split",
            regionId: item.RegionId
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

        _redo.Add(new DraftState(_candidates, _regions));
        var state = _undo[_undo.Count - 1];
        _candidates = state.Candidates;
        _regions = state.Regions;
        _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>重做一次当前图像编辑。</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(new DraftState(_candidates, _regions));
        var state = _redo[_redo.Count - 1];
        _candidates = state.Candidates;
        _regions = state.Regions;
        _redo.RemoveAt(_redo.Count - 1);
    }

    /// <summary>暂存用户选定的当前标签与像素；已有或待存重复标签跳过并报告，不静默替换；无效项中止本次暂存。</summary>
    /// <param name = "libraryId">暂存列表所属的目标字库标识。</param>
    /// <param name = "ids">用户选择的当前候选标识集合。</param>
    /// <param name = "existing">目标字库已有字符标签，用于重复检测。</param>
    /// <param name = "includeExisting">是否允许把已有标签也纳入待替换列表，仍需发布时明确同意替换。</param>
    public IReadOnlyList<string> Stage(
        string libraryId,
        IEnumerable<string> ids,
        IEnumerable<string> existing,
        bool includeExisting = false
    ) => Stage(libraryId, ids, existing, "otsu", includeExisting);

    /// <summary>暂存指定模式的独立参考快照；非法项不会部分写入清单。</summary>
    /// <param name="libraryId">目标字库标识。</param>
    /// <param name="ids">用户选择的候选标识。</param>
    /// <param name="existing">目标字库已有字符。</param>
    /// <param name="binarization">本次参考模式，暂存后不随界面选择改变。</param>
    /// <param name="includeExisting">是否暂存已有标签供显式替换。</param>
    public IReadOnlyList<string> Stage(
        string libraryId,
        IEnumerable<string> ids,
        IEnumerable<string> existing,
        string binarization,
        bool includeExisting = false
    )
    {
        if (binarization != "otsu" && binarization != "fixed" && binarization != "midpoint")
            throw new ArgumentException("Unsupported binarization mode.", nameof(binarization));
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
            if (!DP.Vision.Algorithms.CharacterIdentity.IsGlyph(candidate.Label))
            {
                throw new ArgumentException(
                    "所选字块还有空标签。请在“填写单字”列填入一个中文、字母、数字、标点或符号，再核对暂存。"
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

            added.Add(new GlyphImportItem(candidate.Label, candidate.Image, binarization, Provenance(candidate)));
        }

        if (
            _pending.Count + added.Count > GlyphLibraryLimits.MaximumReferences
            || _pending.Concat(added).Sum(p => (long)p.Image.Width * p.Image.Height)
                > GlyphLibraryLimits.MaximumPixels
        )
        {
            throw new ArgumentException(
                "待入库清单最多4096个独立单字、1600万参考像素，请分库或减少图块尺寸。"
            );
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

    private void Commit(List<GlyphDraftCandidate> next, List<GlyphDraftRegion>? regions = null)
    {
        if (
            next.Count > GlyphLibraryLimits.MaximumReferences
            || next.Sum(c => (long)c.Image.Width * c.Image.Height) > GlyphLibraryLimits.MaximumPixels
        )
        {
            throw new ArgumentException("当前图片候选最多4096个、1600万像素，请分图或减少候选。");
        }

        _undo.Add(new DraftState(_candidates, _regions));
        if (_undo.Count > 20)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        _candidates = next;
        _regions = regions ?? _regions;
    }

    private GlyphDraftRegion Region(string id) =>
        _regions.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException("请选择当前图片的文字ROI。");

    private void ReplaceRegion(GlyphDraftRegion region) =>
        Commit(_candidates, _regions.Select(r => r.Id == region.Id ? region : r).ToList());

    private void CheckRegionBounds(PixelRect bounds)
    {
        RequireImage();
        if (
            !bounds.Fits(_source!)
            || bounds.Width < 4
            || bounds.Height < 4
            || bounds.Width > 6000
            || bounds.Height > 512
            || bounds.Height > bounds.Width * 1.5
        )
            throw new ArgumentException("制作ROI须为原图内的水平单行，宽4–6000、高4–512像素。");
    }

    private static bool Within(PixelRect inner, PixelRect outer) =>
        inner.X >= outer.X
        && inner.Y >= outer.Y
        && (long)inner.X + inner.Width <= (long)outer.X + outer.Width
        && (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

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
        if (label == null || (label.Length != 0 && !DP.Vision.Algorithms.CharacterIdentity.IsGlyph(label)))
        {
            throw new ArgumentException(
                "标签需为空或一个Unicode单字（中文、字母、数字、标点或符号），不能是空格、换行或组合序列。"
            );
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
            + "],\"source_roi_id\":"
            + (candidate.RegionId == null ? "null" : Quote(candidate.RegionId))
            + ",\"source_roi_name\":"
            + (candidate.RegionId == null ? "null" : Quote(Region(candidate.RegionId).Name))
            + ",\"operation\":"
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
            + ",\"user_selected\":true}";
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
