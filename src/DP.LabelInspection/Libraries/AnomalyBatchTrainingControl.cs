using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;

namespace DP.LabelInspection;

/// <summary>
/// 异常模型批量训练页（质量方法B）：载入多张良品图，在每张图上为若干模型画样本框，采集完后一次训练全部模型，
/// 作为一个版本发布到异常模型库。内容固定模型的框统一尺寸并自动对齐到第一个样本；逐字符模型框住整行，自动分割为字符。
/// 采集可保存、下次打开继续。宿主注入模型库管理器、训练实现、字符候选提取及采集文件读写。
/// </summary>
public sealed class AnomalyBatchTrainingControl : UserControl
{
    private static readonly string[] KindNames = { "内容固定", "内容可变", "逐字符" };
    private readonly ComboBox _libraries = new ComboBox
    {
        Width = 280,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly ListBox _images = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ImageViewerControl _viewer = new ImageViewerControl { Dock = DockStyle.Fill };
    private readonly DataGridView _models = Grid();
    private readonly DataGridView _characters = Grid();
    private readonly ListView _coverage = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    private readonly TextBox _confirmed = new TextBox { Width = 200 };
    private readonly Label _status = new Label
    {
        AutoSize = true,
        Text = "添加良品图 → 右侧选中模型 → 在图上框选样本（每图可框多个）→ 训练并发布。",
    };
    private readonly List<Control> _busyDisabled = new List<Control>();
    private AnomalyTrainingSession _session = new AnomalyTrainingSession();
    private IAnomalyLibraryManager? _manager;
    private IAnomalyModelTrainer? _trainer;
    private IGlyphCandidateService? _candidates;
    private IReadOnlyList<InspectionRegion> _recipe = Array.Empty<InspectionRegion>();
    private AnomalyTrainingSample? _selected;
    private bool _filling,
        _extracting;
    private ImageFrame? _shown;

    /// <summary>创建无参数、可安全用于设计器的训练页。</summary>
    public AnomalyBatchTrainingControl()
    {
        Size = new Size(1280, 820);
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        _models.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Name",
                HeaderText = "模型",
                ReadOnly = true,
                Width = 110,
            }
        );
        var kind = new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "训练方式",
            Width = 80,
        };
        kind.Items.AddRange(KindNames);
        _models.Columns.Add(kind);
        _models.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Count",
                HeaderText = "样本",
                ReadOnly = true,
                Width = 45,
            }
        );
        _models.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Note",
                HeaderText = "说明",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            }
        );
        _models.ReadOnly = false;
        _models.CellValueChanged += (_, e) => TryUi(() => KindChanged(e.RowIndex, e.ColumnIndex));
        _models.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_models.IsCurrentCellDirty)
            {
                _models.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _characters.Columns.Add(
            new DataGridViewImageColumn
            {
                Name = "Patch",
                HeaderText = "字符图",
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                Width = 50,
                ReadOnly = true,
            }
        );
        _characters.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Label",
                HeaderText = "身份",
                Width = 45,
            }
        );
        _characters.Columns.Add(
            new DataGridViewCheckBoxColumn
            {
                Name = "Use",
                HeaderText = "作样本",
                Width = 50,
            }
        );
        _characters.ReadOnly = false;
        _characters.CellValueChanged += (_, e) => TryUi(() => CharacterChanged(e.RowIndex, e.ColumnIndex));
        _characters.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_characters.IsCurrentCellDirty && _characters.CurrentCell is DataGridViewCheckBoxCell)
            {
                _characters.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _coverage.Columns.Add("字符", 45);
        _coverage.Columns.Add("样本", 45);
        _coverage.Columns.Add("", 80);

        var top = Bar(DockStyle.Top);
        top.Controls.Add(Caption("异常模型库："));
        top.Controls.Add(_libraries);
        Button(top, "刷新", () => Reload(Head?.Id));
        Button(
            top,
            "新建模型库",
            () =>
            {
                string? name = EditorDialogs.Ask("模型库名称（产品/字体等）", "");
                if (name != null)
                {
                    Reload(Manager.CreateAnomalyLibrary(name));
                }
            }
        );
        Button(top, "打开采集…", OpenProject);
        Button(top, "保存采集…", SaveProject);

        var left = new Panel { Dock = DockStyle.Left, Width = 180 };
        var leftBar = Bar(DockStyle.Bottom);
        Button(leftBar, "添加图像…", AddImages);
        Button(
            leftBar,
            "移除图像",
            () =>
            {
                var image = CurrentImage ?? throw new InvalidOperationException("请选择图像。");
                _session.RemoveImage(image);
                RefreshAll();
            }
        );
        left.Controls.Add(_images);
        left.Controls.Add(leftBar);
        left.Controls.Add(Caption("良品图", DockStyle.Top));

        var right = new Panel { Dock = DockStyle.Right, Width = 380 };
        var modelBar = Bar(DockStyle.Top);
        Button(modelBar, "新建模型…", NewModel);
        Button(
            modelBar,
            "从配方导入",
            () =>
            {
                // 手动导入：也重新加入之前删除过的ROI模型。
                var added = _session.SyncRecipe(_recipe).Concat(_session.ImportRecipe(_recipe)).ToArray();
                RefreshAll();
                _status.Text = $"从配方导入{added.Length}个模型（忽略区跳过，已有同名模型跳过）。";
            }
        );
        Button(
            modelBar,
            "删除模型",
            () =>
            {
                var model = CurrentModel ?? throw new InvalidOperationException("请选择模型。");
                _session.RemoveModel(model);
                RefreshAll();
            }
        );
        _models.Dock = DockStyle.Fill;
        var modelPanel = new Panel { Dock = DockStyle.Fill };
        modelPanel.Controls.Add(_models);
        modelPanel.Controls.Add(modelBar);
        modelPanel.Controls.Add(Caption("模型（选中后在图上框选样本）", DockStyle.Top));
        var coveragePanel = new Panel { Dock = DockStyle.Bottom, Height = 220 };
        coveragePanel.Controls.Add(_coverage);
        coveragePanel.Controls.Add(Caption("逐字符样本数（少于3个标红）", DockStyle.Top));
        right.Controls.Add(modelPanel);
        right.Controls.Add(coveragePanel);

        var charactersPanel = new Panel { Dock = DockStyle.Bottom, Height = 200 };
        var charBar = Bar(DockStyle.Top);
        charBar.Controls.Add(Caption("所选文字行确认文本："));
        charBar.Controls.Add(_confirmed);
        Button(
            charBar,
            "按确认文本重新提取",
            async () =>
            {
                var sample =
                    _selected is { } s && s.Model.Kind == EAnomalyTrainingKind.Characters
                        ? s
                        : throw new InvalidOperationException("请先点选一个逐字符（文字行）样本框。");
                _session.SetConfirmedText(sample, _confirmed.Text);
                RefreshAll();
                await ExtractAsync();
            }
        );
        _characters.Dock = DockStyle.Fill;
        charactersPanel.Controls.Add(_characters);
        charactersPanel.Controls.Add(charBar);
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_viewer);
        center.Controls.Add(charactersPanel);
        center.Controls.Add(
            Caption(
                "在框外拖动：为所选模型新增样本框；拖动框内部移动、拖动边/角调整；Delete删除所选框。内容固定模型的框自动统一尺寸并对齐到第一个样本。",
                DockStyle.Top
            )
        );

        var bottom = Bar(DockStyle.Bottom);
        Button(
            bottom,
            "按配方位置放到本图",
            () =>
            {
                var image = CurrentImage ?? throw new InvalidOperationException("请选择图像。");
                int n = _session.PlaceRecipe(image).Count;
                RefreshAll();
                _status.Text = $"已在本图按配方位置放置{n}个框（内容固定模型已自动对齐），请检查后调整。";
                _ = ExtractAsync();
            }
        );
        Button(
            bottom,
            "按配方位置放到全部图",
            () =>
            {
                int n = _session.Images.Sum(i => _session.PlaceRecipe(i).Count);
                RefreshAll();
                _status.Text = $"已在全部图按配方位置放置{n}个框，请逐图检查后调整。";
                _ = ExtractAsync();
            }
        );
        Button(bottom, "删除所选框", DeleteSelected);
        Button(bottom, "提取字符", async () => await ExtractAsync());
        Button(bottom, "训练并发布新版本", async () => await TrainAsync());
        bottom.Controls.Add(_status);

        Controls.Add(center);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(top);
        Controls.Add(bottom);
        _busyDisabled.AddRange(new Control[] { top, left, right, bottom, charactersPanel, _viewer });

        _images.SelectedIndexChanged += (_, _) => ShowImage();
        _viewer.EditRegions = true;
        _viewer.DrawOutsideRegions = true;
        _viewer.RegionDrawn += (_, e) => TryUi(() => Drawn(e.Bounds));
        _viewer.RegionEdited += (_, e) => TryUi(() => Edited(e.Index, e.Bounds));
        _viewer.SelectedRegionChanged += (_, _) => SelectionChanged();
        _viewer.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                TryUi(DeleteSelected);
            }
        };
    }

    /// <summary>采集会话（与界面分离，可由宿主或测试直接操作）。</summary>
    public AnomalyTrainingSession Session => _session;

    /// <summary>最近一次发布的模型库与版本；配合<see cref = "AnomalyTrainingSession.Bindings"/>把配方ROI绑定到该版本。</summary>
    public (string LibraryId, int Revision)? LastPublished { get; private set; }

    /// <summary>保存采集的实现（宿主提供，参数为会话与文件路径）；未提供时保存按钮不可用。</summary>
    public Action<AnomalyTrainingSession, string>? SaveProjectHandler { get; set; }

    /// <summary>打开采集的实现（宿主提供）：返回会话及各逐字符样本保存时取消的字符序号。</summary>
    public Func<
        string,
        (AnomalyTrainingSession Session, IReadOnlyDictionary<AnomalyTrainingSample, int[]> Excluded)
    >? LoadProjectHandler { get; set; }

    private readonly Dictionary<AnomalyTrainingSample, int[]> _restoreExcluded =
        new Dictionary<AnomalyTrainingSample, int[]>();

    /// <summary>连接宿主服务：模型库管理器、训练实现及字符候选提取（没有时逐字符模型无法提取）。</summary>
    /// <param name = "manager">异常模型库管理器。</param>
    /// <param name = "trainer">训练实现。</param>
    /// <param name = "candidates">字符候选提取服务（OCR+分割）。</param>
    public void AttachServices(
        IAnomalyLibraryManager manager,
        IAnomalyModelTrainer trainer,
        IGlyphCandidateService? candidates
    )
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _trainer = trainer ?? throw new ArgumentNullException(nameof(trainer));
        _candidates = candidates;
        Reload();
    }

    /// <summary>
    /// 设置当前配方的ROI（每次打开训练页时调用）：新增的ROI自动建同名模型，已有模型关联到最新的ROI配置；
    /// 人工删除过的模型不再自动添加。
    /// </summary>
    /// <param name = "regions">配方ROI。</param>
    public void SetRecipe(IEnumerable<InspectionRegion> regions)
    {
        _recipe = (regions ?? throw new ArgumentNullException(nameof(regions))).ToArray();
        var added = _session.SyncRecipe(_recipe);
        RefreshAll();
        if (added.Count > 0 && _session.Samples.Count > 0)
        {
            _status.Text =
                "配方中新增的ROI已加入模型列表：" + string.Join("、", added.Select(m => m.Name)) + "。";
        }
    }

    /// <summary>加入一张良品图。</summary>
    /// <param name = "image">不可变整图。</param>
    /// <param name = "name">显示名称。</param>
    /// <param name = "path">来源文件，可为null。</param>
    public void AddImage(ImageFrame image, string name, string? path = null)
    {
        _session.AddImage(image, name, path);
        RefreshAll();
        _images.SelectedIndex = _session.Images.Count - 1;
    }

    /// <summary>对全部待提取的文字行样本提取字符（后台依次进行，界面可继续框选）。</summary>
    public async Task ExtractAsync()
    {
        if (_extracting || _candidates == null)
        {
            if (_candidates == null && _session.Samples.Any(s => s.NeedsExtraction))
            {
                _status.Text = "宿主未提供字符提取服务（需要检测引擎和OCR），逐字符样本无法提取。";
            }

            return;
        }

        _extracting = true;
        try
        {
            while (_session.Samples.Any(s => s.NeedsExtraction))
            {
                await _session.ExtractAsync(
                    _candidates,
                    new Progress<(int Done, int Total)>(p =>
                        _status.Text = $"正在提取字符 {p.Done}/{p.Total}…"
                    )
                );
                foreach (var pair in _restoreExcluded.ToArray())
                {
                    if (pair.Key.Segmentation != null)
                    {
                        foreach (int i in pair.Value.Where(i => i < pair.Key.Include.Count))
                        {
                            _session.SetInclude(pair.Key, i, false);
                        }

                        _restoreExcluded.Remove(pair.Key);
                    }
                }
            }

            // 让已排队的进度回调先执行，避免覆盖完成信息。
            await Task.Yield();
            RefreshAll();
            var problems = _session.Samples.Where(s => s.Problem != null).ToArray();
            _status.Text =
                problems.Length == 0
                    ? "字符提取完成。"
                    : $"字符提取完成；{problems.Length}行需复核（点选该行的框查看）。";
        }
        finally
        {
            _extracting = false;
        }
    }

    private async Task TrainAsync()
    {
        var head = Head ?? throw new InvalidOperationException("请选择或新建异常模型库。");
        var trainer = _trainer ?? throw new InvalidOperationException("宿主未提供训练实现。");
        if (head.Archived)
        {
            throw new InvalidOperationException("已归档的模型库不能添加模型，请先恢复。");
        }

        if (_session.Samples.Count == 0)
        {
            throw new InvalidOperationException("还没有样本框。");
        }

        var problems = _session.Problems();
        var few = _session.CharacterCoverage().Where(c => c.Value < 3).Select(c => c.Key).ToArray();
        if (
            (problems.Count > 0 || few.Length > 0)
            && MessageBox.Show(
                this,
                string.Join("\r\n", problems.Take(12))
                    + (problems.Count > 12 ? $"\r\n……共{problems.Count}项" : "")
                    + (few.Length > 0 ? $"\r\n样本少于3个的字符（阈值可能偏紧）：{string.Join("", few)}" : "")
                    + "\r\n\r\n仍然训练？（有问题的行不参与训练）",
                "训练前检查",
                MessageBoxButtons.OKCancel
            ) != DialogResult.OK
        )
        {
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(m => _status.Text = m);
            var session = _session;
            var entries = await Task.Run(() => session.Train(trainer, progress));
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("没有可训练的样本。");
            }

            string provenance = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"images\":{0},\"samples\":{1},\"utc\":\"{2:O}\"}}",
                _session.Images.Count,
                _session.Samples.Count,
                DateTimeOffset.UtcNow
            );
            int revision = Manager.PutAnomalyModels(head.Id, head.Revision, entries, true, provenance);
            LastPublished = (head.Id, revision);
            Reload(head.Id);
            _status.Text =
                $"已发布 {head.Name} r{revision}：{entries.Count}个模型（整ROI {entries.Count(e => e.Scope == EAnomalyModelScope.Region)}个，字符 {entries.Count(e => e.Scope == EAnomalyModelScope.Character)}个）。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Drawn(PixelRect bounds)
    {
        var image = CurrentImage ?? throw new InvalidOperationException("请先添加并选择图像。");
        var model =
            CurrentModel ?? throw new InvalidOperationException("请先在右侧选择或新建模型，再在图上框选。");
        var sample = _session.AddSample(image, model, bounds);
        _selected = sample;
        RefreshAll();
        if (model.Kind == EAnomalyTrainingKind.Characters)
        {
            _ = ExtractAsync();
        }
    }

    private void Edited(int index, PixelRect bounds)
    {
        var image = CurrentImage;
        if (image == null)
        {
            return;
        }

        var samples = _session.SamplesOn(image);
        if (index < 0 || index >= samples.Count)
        {
            return;
        }

        _session.MoveSample(samples[index], bounds);
        _selected = samples[index];
        RefreshAll();
        if (samples[index].Model.Kind == EAnomalyTrainingKind.Characters)
        {
            _ = ExtractAsync();
        }
    }

    private void SelectionChanged()
    {
        var image = CurrentImage;
        int index = _viewer.SelectedRegionIndex;
        var samples = image == null ? Array.Empty<AnomalyTrainingSample>() : _session.SamplesOn(image);
        _selected = index >= 0 && index < samples.Count ? samples[index] : null;
        if (_selected != null)
        {
            SelectModelRow(_selected.Model);
        }

        ShowCharacters();
    }

    private void DeleteSelected()
    {
        var sample = _selected ?? throw new InvalidOperationException("请先点选要删除的框。");
        _session.RemoveSample(sample);
        _selected = null;
        RefreshAll();
    }

    private void NewModel()
    {
        string? name = EditorDialogs.Ask(
            "模型名称（对应配方ROI时用同名）",
            "模型" + (_session.Models.Count + 1)
        );
        if (name == null)
        {
            return;
        }

        var region = _recipe.FirstOrDefault(r => r.Name == name && r.Kind != ERegionKind.Ignore);
        var kind =
            region?.Kind == ERegionKind.Text && region.Field.Expected == null
                ? EAnomalyTrainingKind.Characters
            : region?.Kind == ERegionKind.Barcode ? EAnomalyTrainingKind.VariableContent
            : EAnomalyTrainingKind.FixedContent;
        var model = _session.AddModel(name.Trim(), kind, region);
        RefreshAll();
        SelectModelRow(model);
        _status.Text =
            region == null
                ? $"已新建模型“{model.Name}”（不对应配方ROI，只发布模型）；可在列表中改训练方式。"
                : $"已新建模型“{model.Name}”，对应配方ROI，发布后可一键绑定。";
    }

    private void KindChanged(int row, int column)
    {
        if (_filling || row < 0 || _models.Columns[column].Name != "Kind")
        {
            return;
        }

        if (!(_models.Rows[row].Tag is AnomalyTrainingModel model))
        {
            return;
        }

        int kind = Array.IndexOf(KindNames, _models.Rows[row].Cells[column].Value as string);
        if (kind >= 0)
        {
            _session.SetKind(model, (EAnomalyTrainingKind)kind);
            RefreshAll();
            _ = ExtractAsync();
        }
    }

    private void CharacterChanged(int row, int column)
    {
        if (_filling || row < 0 || _selected?.Segmentation == null)
        {
            return;
        }

        var cell = _characters.Rows[row].Cells[column];
        if (_characters.Columns[column].Name == "Label")
        {
            try
            {
                _session.SetLabel(_selected, row, (cell.Value as string ?? "").Trim());
            }
            catch (ArgumentException)
            {
                _filling = true;
                cell.Value = _selected.Labels[row];
                _filling = false;
                throw;
            }
        }
        else if (_characters.Columns[column].Name == "Use")
        {
            _session.SetInclude(_selected, row, cell.Value is bool b && b);
        }

        RefreshCoverage();
    }

    private void AddImages()
    {
        using var d = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg", Multiselect = true };
        if (d.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        foreach (string file in d.FileNames)
        {
            using var image = Image.FromFile(file);
            _session.AddImage(DrawingImageConverter.FromImage(image), Path.GetFileName(file), file);
        }

        RefreshAll();
        _images.SelectedIndex = _session.Images.Count - 1;
        _status.Text = $"已添加{d.FileNames.Length}张图（共{_session.Images.Count}张）。";
    }

    private void SaveProject()
    {
        var save = SaveProjectHandler ?? throw new InvalidOperationException("宿主未提供采集保存功能。");
        using var d = new SaveFileDialog { Filter = "批量训练采集|*.json", FileName = "异常模型采集.json" };
        if (d.ShowDialog() == DialogResult.OK)
        {
            save(_session, d.FileName);
            _status.Text = "采集已保存：" + d.FileName;
        }
    }

    private void OpenProject()
    {
        var load = LoadProjectHandler ?? throw new InvalidOperationException("宿主未提供采集打开功能。");
        using var d = new OpenFileDialog { Filter = "批量训练采集|*.json" };
        if (d.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var (session, excluded) = load(d.FileName);
        _session = session;
        _restoreExcluded.Clear();
        foreach (var pair in excluded)
        {
            _restoreExcluded[pair.Key] = pair.Value;
        }

        _selected = null;
        _shown = null;
        RefreshAll();
        if (_session.Images.Count > 0)
        {
            _images.SelectedIndex = 0;
        }

        _status.Text =
            $"已打开采集：{_session.Images.Count}张图、{_session.Models.Count}个模型、{_session.Samples.Count}个框。";
        _ = ExtractAsync();
    }

    private AnomalyTrainingImage? CurrentImage =>
        _images.SelectedIndex >= 0 && _images.SelectedIndex < _session.Images.Count
            ? _session.Images[_images.SelectedIndex]
            : null;

    private AnomalyTrainingModel? CurrentModel => _models.CurrentRow?.Tag as AnomalyTrainingModel;

    private IAnomalyLibraryManager Manager =>
        _manager ?? throw new InvalidOperationException("未连接异常模型库管理器。");

    private AnomalyLibraryInfo? Head => _libraries.SelectedItem as AnomalyLibraryInfo;

    private void SelectModelRow(AnomalyTrainingModel model)
    {
        foreach (DataGridViewRow row in _models.Rows)
        {
            if (row.Tag == model)
            {
                _models.CurrentCell = row.Cells[0];
                return;
            }
        }
    }

    private void RefreshAll()
    {
        var current = CurrentModel;
        _filling = true;
        try
        {
            int imageIndex = _images.SelectedIndex;
            _images.Items.Clear();
            foreach (var image in _session.Images)
            {
                int n = _session.SamplesOn(image).Count;
                _images.Items.Add(image.Name + (n > 0 ? $"（{n}框）" : ""));
            }

            if (_images.Items.Count > 0)
            {
                _images.SelectedIndex = Math.Min(Math.Max(0, imageIndex), _images.Items.Count - 1);
            }

            _models.Rows.Clear();
            foreach (var model in _session.Models)
            {
                var samples = _session.Of(model);
                string note =
                    model.Kind == EAnomalyTrainingKind.FixedContent && model.Width != null
                        ? $"{model.Width}×{model.Height}"
                    : model.Kind == EAnomalyTrainingKind.Characters
                        ? $"{samples.Count(s => s.Segmentation != null)}行已提取"
                            + (
                                samples.Any(s => s.Problem != null)
                                    ? $"，{samples.Count(s => s.Problem != null)}行需复核"
                                    : ""
                            )
                    : "";
                note += model.Region == null ? "（独立）" : "";
                int row = _models.Rows.Add(model.Name, KindNames[(int)model.Kind], samples.Count, note);
                _models.Rows[row].Tag = model;
            }
        }
        finally
        {
            _filling = false;
        }

        if (current != null && _session.Models.Contains(current))
        {
            SelectModelRow(current);
        }

        if (_selected != null && !_session.Samples.Contains(_selected))
        {
            _selected = null;
        }

        ShowImage();
        RefreshCoverage();
    }

    private void ShowImage()
    {
        var image = CurrentImage;
        if (image == null)
        {
            _viewer.SetImage(null);
            _shown = null;
            return;
        }

        if (!ReferenceEquals(_shown, image.Image))
        {
            _viewer.SetImage(image.Image);
            _shown = image.Image;
        }

        var samples = _session.SamplesOn(image);
        var counters = new Dictionary<AnomalyTrainingModel, int>();
        _viewer.SetOverlays(
            samples
                .Select(s =>
                {
                    counters[s.Model] = counters.TryGetValue(s.Model, out int n) ? n + 1 : 1;
                    string name = s.Model.Name + (counters[s.Model] > 1 ? "#" + counters[s.Model] : "");
                    return s.Model.Kind switch
                    {
                        EAnomalyTrainingKind.Characters => new InspectionRegion(
                            name,
                            ERegionKind.Text,
                            s.Bounds,
                            true
                        ),
                        EAnomalyTrainingKind.VariableContent => new InspectionRegion(
                            name,
                            ERegionKind.Barcode,
                            s.Bounds
                        ),
                        _ => new InspectionRegion(name, ERegionKind.Fixed, s.Bounds),
                    };
                })
                .ToArray(),
            Array.Empty<InspectionFinding>()
        );
        _viewer.SetCharacters(
            samples.Where(s => s.Segmentation != null).SelectMany(s => s.Segmentation!.Characters)
        );
        int selected = _selected == null ? -1 : samples.ToList().IndexOf(_selected);
        _viewer.SelectedRegionIndex = selected;
        ShowCharacters();
    }

    private void ShowCharacters()
    {
        // 可能在RefreshAll填表过程中被调用，结束时恢复原状态而不是直接清除。
        bool filling = _filling;
        _filling = true;
        try
        {
            foreach (DataGridViewRow row in _characters.Rows)
            {
                (row.Cells["Patch"].Value as Image)?.Dispose();
            }

            _characters.Rows.Clear();
            var sample = _selected;
            _confirmed.Text =
                sample?.ConfirmedText ?? (sample?.Segmentation != null ? string.Concat(sample.Labels) : "");
            if (sample?.Segmentation == null)
            {
                return;
            }

            for (int i = 0; i < sample.Segmentation.Characters.Count; i++)
            {
                _characters.Rows.Add(
                    DrawingImageConverter.ToBitmap(sample.Segmentation.Characters[i].Patch),
                    sample.Labels[i],
                    sample.Include[i]
                );
            }
        }
        finally
        {
            _filling = filling;
        }
    }

    private void RefreshCoverage()
    {
        _coverage.Items.Clear();
        foreach (var pair in _session.CharacterCoverage())
        {
            var item = new ListViewItem(
                new[]
                {
                    pair.Key,
                    pair.Value.ToString(CultureInfo.InvariantCulture),
                    pair.Value < 3 ? "偏少" : "",
                }
            );
            if (pair.Value < 3)
            {
                item.ForeColor = Color.FromArgb(198, 40, 40);
            }

            _coverage.Items.Add(item);
        }
    }

    private void Reload(string? id = null)
    {
        if (_manager == null)
        {
            return;
        }

        var items = Manager.ListAnomalyLibraries();
        _libraries.Items.Clear();
        foreach (var item in items)
        {
            _libraries.Items.Add(item);
        }

        if (items.Count > 0)
        {
            _libraries.SelectedItem = items.FirstOrDefault(i => i.Id == id) ?? items[0];
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in _busyDisabled)
        {
            c.Enabled = !busy;
        }

        UseWaitCursor = busy;
    }

    private void TryUi(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "批量训练操作未完成");
            RefreshAll();
        }
    }

    private static DataGridView Grid()
    {
        return new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
    }

    private static FlowLayoutPanel Bar(DockStyle dock)
    {
        return new FlowLayoutPanel { Dock = dock, AutoSize = true };
    }

    private static Label Caption(string text, DockStyle dock = DockStyle.None)
    {
        return new Label
        {
            Text = text,
            AutoSize = dock == DockStyle.None,
            Dock = dock,
            Height =
                dock == DockStyle.None ? 0
                : text.Length > 40 ? 36
                : 20,
            Margin = new Padding(3, 7, 0, 0),
            ForeColor = Color.FromArgb(70, 70, 70),
        };
    }

    private void Button(Control parent, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "批量训练操作未完成");
                RefreshAll();
            }
        };
        parent.Controls.Add(b);
    }

    private void Button(Control parent, string text, Action action)
    {
        Button(
            parent,
            text,
            () =>
            {
                action();
                return Task.CompletedTask;
            }
        );
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (DataGridViewRow row in _characters.Rows)
            {
                (row.Cells["Patch"].Value as Image)?.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
