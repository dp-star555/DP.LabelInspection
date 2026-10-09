using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>增量多图、多ROI参考工作台，支持单行区域隔离、手动分割和复核后暂存。</summary>
public sealed class GlyphQuickBuilderControl : UserControl
{
    private readonly GlyphDraftSession _draft = new GlyphDraftSession();
    private readonly ImageViewerControl _viewer = new ImageViewerControl
    {
        Dock = DockStyle.Fill,
        ShowFindingLabels = false,
    };
    private readonly DataGridView _grid = Table(false),
        _pending = Table(true);
    private readonly ModernSelect _libraries = new ModernSelect { Width = 230 };
    private readonly ModernSelect _mode = new ModernSelect { Width = 190 };
    private readonly ModernInput _text = new ModernInput { Width = 200, MaxLength = 256 };
    private readonly ModernSelect _regionsBox = new ModernSelect { Width = 240 };
    private readonly ModernSelect _binarization = new ModernSelect { Width = 130 };
    private readonly FlowLayoutPanel _regionTools = Bar();
    private readonly ModernButton _extractAll = new ModernButton
    {
        Text = "提取全部ROI",
        AutoSize = true,
        Enabled = false,
    };
    private readonly ModernButton _recognize = new ModernButton
        {
            Text = "提取当前ROI（OCR）",
            AutoSize = true,
            Enabled = false,
        },
        _segment = new ModernButton
        {
            Text = "按文字重分割",
            AutoSize = true,
            Enabled = false,
        },
        _cancelButton = new ModernButton
        {
            Text = "取消提取",
            AutoSize = true,
            Enabled = false,
        };
    private readonly Label _status = new Label
    {
        Dock = DockStyle.Bottom,
        Height = 58,
        Padding = new Padding(8),
        BackColor = ModernTheme.Dark.Container,
        Text = "① 新建/选择字库 → ② 从任意多张图补字 → ③ 核对后暂存 → ④ 保存清单。不要求一张图包含所有字符。",
    };
    private readonly ModernTextArea _coverage = new ModernTextArea
    {
        Dock = DockStyle.Top,
        Height = 112,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly Label _sourceName = new Label { AutoSize = true, Text = "尚未导入图片" };
    private readonly Label _pendingTitle = new Label
    {
        Dock = DockStyle.Top,
        Height = 32,
        Text = "④ 待入库清单（0） · 换图保留",
        Padding = new Padding(4),
    };
    private readonly ModernListBox _saved = new ModernListBox { Dock = DockStyle.Top, Height = 90 };
    private readonly PictureBox _savedPreview = new PictureBox
    {
        Dock = DockStyle.Top,
        Height = 80,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.White,
    };
    private readonly Label _extractionHint = new Label { AutoSize = true };
    private readonly ModernCheckbox _replace = new ModernCheckbox
    {
        Text = "本次允许替换库中已有字符",
        Width = 280,
        Height = 34,
    };
    private readonly FlowLayoutPanel _tools = Bar(),
        _editTools = Bar(),
        _reviewTools = Bar();
    private readonly Dictionary<string, string> _pendingNames = new Dictionary<string, string>(
        StringComparer.Ordinal
    );
    private IGlyphLibraryManager? _manager;
    private IGlyphCandidateService? _extractor;
    private GlyphLibrarySnapshot? _library;
    private PixelSnapshot? _image;
    private string? _selectedRegionId;
    private GlyphCandidateExtraction? _result;
    private string _currentName = "图片";
    private CancellationTokenSource? _cancel;
    private Task? _work;
    private bool _syncing;
    private Point? _cutGuide;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    /// <summary>创建可安全用于设计器的页面，连接的服务由宿主拥有。</summary>
    public GlyphQuickBuilderControl()
    {
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        Size = new Size(1220, 850);
        _tools.Controls.Add(new Label { Text = "① 目标字库", AutoSize = true });
        _tools.Controls.Add(_libraries);
        Button(
            _tools,
            "＋ 新建字库",
            () =>
            {
                var name = EditorDialogs.Ask("字形类别（字体/宽窄/工艺不同请分别建库）", "");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (_draft.Pending.Count > 0)
                    {
                        throw new InvalidOperationException("请先保存或清空当前待入库清单，再新建字库。");
                    }

                    Reload(Manager.CreateLibrary(name!));
                }
            }
        );
        Button(_tools, "刷新字库", () => Reload());
        Button(_tools, "② 导入 / 下一张图", ImportImage);
        _tools.Controls.Add(_sourceName);
        _binarization.Items.AddRange(new object[] { "otsu", "fixed", "midpoint" });
        _binarization.SelectedIndex = 0;
        _tools.Controls.Add(new Label { Text = "参考二值化", AutoSize = true });
        _tools.Controls.Add(_binarization);
        _regionTools.Controls.Add(new Label { Text = "本图文字ROI", AutoSize = true });
        _regionTools.Controls.Add(_regionsBox);
        Button(_regionTools, "框下一行", () => _mode.SelectedIndex = 0);
        Button(_regionTools, "调整文字ROI", () => _mode.SelectedIndex = 4);
        Button(_regionTools, "精确改ROI", EditRegionBounds);
        Button(_regionTools, "删除当前ROI", () => RemoveCurrentRegion());
        Button(
            _regionTools,
            "清空文字ROI",
            () =>
            {
                if (
                    ConfirmReplacing(
                        _draft.Candidates.Any(c => c.RegionId != null),
                        "清空本图全部文字ROI及其未暂存候选？独立手工裁图和右侧清单保留。"
                    )
                )
                {
                    _draft.ClearRegions();
                    RefreshCandidates();
                }
            }
        );
        _regionsBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _regionsBox.SelectedItem is GlyphDraftRegion region)
                TryUi(() => SelectRegion(region.Id));
        };
        var recognition = Bar();
        _recognize.Click += async (_, __) => await RunFromUiAsync(false);
        recognition.Controls.Add(_recognize);
        recognition.Controls.Add(new Label { Text = "行文字纠错（可选）", AutoSize = true });
        recognition.Controls.Add(_text);
        _segment.Click += async (_, __) => await RunFromUiAsync(true);
        recognition.Controls.Add(_segment);
        _extractAll.Click += async (_, _) => await RunAllFromUiAsync();
        recognition.Controls.Add(_extractAll);
        _cancelButton.Click += (_, __) => _cancel?.Cancel();
        recognition.Controls.Add(_cancelButton);
        recognition.Controls.Add(_extractionHint);
        _mode.Items.AddRange(
            new object[]
            {
                "框选文字行（可多ROI）",
                "手工补画字块",
                "调整候选框",
                "手动切开（点切线）",
                "调整文字行ROI",
            }
        );
        _mode.SelectedIndex = 0;
        _editTools.Controls.Add(_mode);
        Button(_editTools, "精确改框 X/Y/宽/高", EditBounds);
        Button(
            _editTools,
            "删除选中候选",
            () =>
            {
                _draft.Remove(Selected().Id);
                RefreshCandidates();
            }
        );
        Button(
            _editTools,
            "撤销调整",
            () =>
            {
                _draft.Undo();
                RefreshCandidates();
            }
        );
        Button(
            _editTools,
            "重做",
            () =>
            {
                _draft.Redo();
                RefreshCandidates();
            }
        );
        Button(_editTools, "适应窗口", () => _viewer.FitToWindow());
        _editTools.Controls.Add(
            new Label { Text = "粘连：补画整块 → 选中该块 → 切开模式点击分界 → 分别标字", AutoSize = true }
        );
        _grid.Columns.Add(
            new DataGridViewCheckBoxColumn
            {
                Name = "Use",
                HeaderText = "选用",
                FillWeight = 35,
            }
        );
        _grid.Columns.Add(
            new DataGridViewImageColumn
            {
                Name = "Patch",
                HeaderText = "当前图候选",
                ReadOnly = true,
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                FillWeight = 65,
            }
        );
        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Character",
                HeaderText = "填写单字",
                MaxInputLength = 2,
                FillWeight = 50,
            }
        );
        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Details",
                HeaderText = "来源ROI / 入库状态 / 分割方式 / 原图坐标",
                ReadOnly = true,
                FillWeight = 210,
            }
        );
        Button(_reviewTools, "只选库中缺字", () => SelectCandidates(true));
        Button(_reviewTools, "选择有标签项", () => SelectCandidates(false));
        Button(
            _reviewTools,
            "取消选择",
            () =>
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    row.Cells["Use"].Value = false;
                }
            }
        );
        Button(_reviewTools, "③ 加入待入库清单 →", () => StageSelected());
        _pending.Columns.Add(
            new DataGridViewImageColumn
            {
                Name = "Patch",
                HeaderText = "待存图块",
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                FillWeight = 65,
            }
        );
        _pending.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Character",
                HeaderText = "字符",
                FillWeight = 40,
            }
        );
        _pending.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Source",
                HeaderText = "来源图 / ROI",
                FillWeight = 100,
            }
        );
        var pendingTools = Bar();
        Button(
            pendingTools,
            "移除清单选中项",
            () =>
            {
                if (_pending.CurrentRow?.Tag is GlyphImportItem item)
                {
                    _draft.RemovePending(item.Character);
                    _pendingNames.Remove(item.Character);
                    RefreshPending();
                    UpdateRowDetails();
                }
            }
        );
        Button(
            pendingTools,
            "清空清单",
            () =>
            {
                if (
                    _draft.Pending.Count > 0
                    && MessageBox.Show(
                        this,
                        "丢弃尚未保存的清单？已保存字库不受影响。",
                        "清空待入库清单",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    ) == DialogResult.Yes
                )
                {
                    _draft.ClearPending();
                    _pendingNames.Clear();
                    RefreshPending();
                    UpdateRowDetails();
                }
            }
        );
        pendingTools.Controls.Add(_replace);
        Button(pendingTools, "④ 保存清单到字库", () => SavePending());
        var right = new Panel { Dock = DockStyle.Fill };
        var savedTools = Bar();
        Button(savedTools, "编辑选中单字", () =>
        {
            if (_saved.SelectedItem is not string character)
                throw new InvalidOperationException("请在已收录列表选择单字。");
            if (ConfirmReplacing(_draft.Candidates.Count > 0,
                "载入该单字替换当前图候选？待入库清单仍保留。"))
                EditStoredGlyph(character);
        });
        var savedTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "字库已收录（点击预览）",
            Padding = new Padding(4),
        };
        right.Controls.Add(_pending);
        right.Controls.Add(pendingTools);
        right.Controls.Add(_pendingTitle);
        right.Controls.Add(_savedPreview);
        right.Controls.Add(savedTools);
        right.Controls.Add(_saved);
        right.Controls.Add(_coverage);
        right.Controls.Add(savedTitle);
        var imagePanel = new Panel { Dock = DockStyle.Fill };
        imagePanel.Controls.Add(_viewer);
        imagePanel.Controls.Add(_editTools);
        imagePanel.Controls.Add(recognition);
        imagePanel.Controls.Add(_regionTools);
        var candidatesPanel = new Panel { Dock = DockStyle.Fill };
        candidatesPanel.Controls.Add(_grid);
        candidatesPanel.Controls.Add(_reviewTools);
        var vertical = new ModernSplitter
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            InitialPanel2Size = 0,
            Orientation = Orientation.Horizontal,
            Size = new Size(850, 720),
            SplitterDistance = 405,
        };
        vertical.Panel1.Controls.Add(imagePanel);
        vertical.Panel2.Controls.Add(candidatesPanel);
        var columns = new ModernSplitter
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            InitialPanel2Size = 0,
            Size = new Size(1220, 780),
            SplitterDistance = 850,
        };
        columns.Panel1.Controls.Add(vertical);
        columns.Panel2.Controls.Add(right);
        Controls.Add(columns);
        Controls.Add(_status);
        Controls.Add(_tools);
        InspectionUiStyle.Apply(this);
        _mode.SelectedIndexChanged += (_, __) =>
        {
            UpdateMode();
            Say(
                _mode.SelectedIndex == 3
                    ? "先在表格选中粘连块，再在图上点击垂直切线。两侧标签将清空，须分别填写；切线两侧至少各4像素。"
                : _mode.SelectedIndex == 1
                    ? "在原图拖框补充一个字；也可先框一对粘连字，再切开。自动分割为0也能使用。"
                : _mode.SelectedIndex == 2
                    ? "点击候选框后拖动边/角调整，或用“精确改框”。调整从原图重新裁取，须重新核对邻字墨迹。"
                : _mode.SelectedIndex == 4 ? "拖动文字行ROI的边界；只更新该ROI，其他ROI和清单保留。"
                : "在图上连续框多行文字；每个ROI独立保留文字和候选。可逐条或提取全部ROI。"
            );
        };
        _libraries.SelectedIndexChanged += (_, __) => TryUi(LibraryChanged);
        _saved.SelectedIndexChanged += (_, __) =>
        {
            var old = _savedPreview.Image;
            _savedPreview.Image =
                _saved.SelectedItem is string key && _library != null
                    ? DrawingImageConverter.ToBitmap(_library.Glyphs[key].Image)
                    : null;
            old?.Dispose();
        };
        _text.TextChanged += (_, __) =>
        {
            if (!_syncing && !IsBusy && SelectedRegion is { } region)
            {
                _draft.SetRegionText(region.Id, _text.Text);
                RefreshRegions(region.Id, updateText: false);
            }
        };
        _grid.CurrentCellDirtyStateChanged += (_, __) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_syncing || e.RowIndex < 0)
            {
                return;
            }

            var row = _grid.Rows[e.RowIndex];
            if (_grid.Columns[e.ColumnIndex].Name == "Character" && row.Tag is GlyphDraftCandidate candidate)
            {
                try
                {
                    _draft.SetLabel(
                        candidate.Id,
                        Convert.ToString(row.Cells["Character"].Value, CultureInfo.InvariantCulture) ?? ""
                    );
                    row.Tag = _draft.Candidates.First(c => c.Id == candidate.Id);
                    UpdateRowDetails();
                    UpdateViewer();
                }
                catch (Exception error)
                {
                    _syncing = true;
                    row.Cells["Character"].Value = candidate.Label;
                    _syncing = false;
                    Say(error.Message, true);
                }
            }
        };
        _grid.SelectionChanged += (_, __) =>
        {
            if (!_syncing && _grid.CurrentRow?.Tag is GlyphDraftCandidate candidate)
            {
                if (candidate.RegionId != null && candidate.RegionId != _selectedRegionId)
                    RefreshRegions(candidate.RegionId);
                _viewer.FocusRegion(candidate.Bounds);
            }
        };
        _viewer.RegionDrawn += (_, e) =>
        {
            if (IsBusy)
            {
                return;
            }

            TryUi(() =>
            {
                if (_mode.SelectedIndex == 1)
                {
                    AddManualCandidate(e.Bounds);
                }
                else if (_mode.SelectedIndex == 0)
                {
                    SetRegion(e.Bounds);
                }
                else
                {
                    Say("要补字请切换到“手工补画字块”；调整模式请拖现有框的边/角。", true);
                }
            });
        };
        _viewer.RegionEdited += (_, e) =>
            TryUi(() =>
            {
                if (_mode.SelectedIndex == 4 && e.Index >= 0 && e.Index < _draft.Regions.Count)
                {
                    var region = _draft.Regions[e.Index];
                    try
                    {
                        if (
                            ConfirmReplacing(
                                _draft.Candidates.Any(c => c.RegionId == region.Id),
                                "调整该ROI将清除它的旧候选；其他ROI和已暂存清单保留。继续？"
                            )
                        )
                            _draft.SetRegionBounds(region.Id, e.Bounds);
                    }
                    finally
                    {
                        RefreshCandidates();
                    }
                }
                else if (_mode.SelectedIndex == 2 && e.Index >= 0 && e.Index < _draft.Candidates.Count)
                {
                    string id = _draft.Candidates[e.Index].Id;
                    _draft.Resize(id, e.Bounds);
                    RefreshCandidates(id);
                }
            });
        _viewer.SelectedRegionChanged += (_, _) =>
        {
            if (
                !_syncing
                && !IsBusy
                && _mode.SelectedIndex == 4
                && _viewer.SelectedRegionIndex >= 0
                && _viewer.SelectedRegionIndex < _draft.Regions.Count
            )
            {
                var region = _draft.Regions[_viewer.SelectedRegionIndex];
                if (region.Id != _selectedRegionId)
                    TryUi(() => SelectRegion(region.Id));
            }
        };
        _viewer.MouseDown += (_, e) =>
        {
            if (
                !IsBusy
                && _mode.SelectedIndex == 3
                && e.Button == MouseButtons.Left
                && _viewer.ClientToImage(e.Location) is Point point
            )
            {
                TryUi(() =>
                {
                    var selected = Selected();
                    if (point.Y < selected.Bounds.Y || point.Y >= selected.Bounds.Y + selected.Bounds.Height)
                    {
                        throw new ArgumentException("请在选中候选块内点击切线。");
                    }

                    SplitSelected(point.X);
                });
            }
        };
        _viewer.MouseMove += (_, e) =>
        {
            if (_mode.SelectedIndex == 3 && !IsBusy)
            {
                _cutGuide = _viewer.ClientToImage(e.Location);
                _viewer.Invalidate();
            }
        };
        _viewer.MouseLeave += (_, __) =>
        {
            _cutGuide = null;
            _viewer.Invalidate();
        };
        _viewer.ForegroundPaint += (_, e) =>
        {
            if (
                _mode.SelectedIndex != 3
                || !_cutGuide.HasValue
                || _grid.CurrentRow?.Tag is not GlyphDraftCandidate c
            )
            {
                return;
            }

            var p = _cutGuide.Value;
            var box = c.Bounds;
            if (p.X < box.X || p.X > box.X + box.Width || p.Y < box.Y || p.Y > box.Y + box.Height)
            {
                return;
            }

            var a = _viewer.ImageToClient(new PointF(p.X, box.Y));
            var b = _viewer.ImageToClient(new PointF(p.X, box.Y + box.Height));
            using var pen = new Pen(
                p.X - box.X >= 4 && box.X + box.Width - p.X >= 4 ? Color.DarkOrange : Color.Crimson,
                2
            )
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
            };
            e.Graphics.DrawLine(pen, a, b);
        };
        UpdateMode();
        UpdateCoverage();
    }

    /// <summary>是否有原生提取任务正在执行。</summary>
    public bool IsBusy => _cancel != null;

    /// <summary>最近的原始自动结果；手动编辑保存在候选表，不修改此证据快照。</summary>
    public GlyphCandidateExtraction? LastExtraction => _result;

    /// <summary>当前图已登记的独立文字行ROI，不含已暂存的其他来源图区域。</summary>
    public IReadOnlyList<GlyphDraftRegion> Regions => _draft.Regions;

    /// <summary>当前选中的制作区域标识，无区域时为null。</summary>
    public string? SelectedRegionId => _selectedRegionId;

    private GlyphDraftRegion? SelectedRegion => _draft.Regions.FirstOrDefault(r => r.Id == _selectedRegionId);

    /// <summary>当前制库目标的类别标识，无字库时为空。</summary>
    public string? SelectedLibraryId => (_libraries.SelectedItem as GlyphLibraryInfo)?.Id;

    /// <summary>刷新最新字库并选择目标，不清除当前图候选或待入库清单。</summary>
    /// <param name="selectedLibrary">要选择的类别标识；null保留当前目标。</param>
    public void RefreshLibraries(string? selectedLibrary = null)
    {
        Idle();
        if (_draft.PendingLibraryId != null && selectedLibrary != null
            && selectedLibrary != _draft.PendingLibraryId)
            throw new InvalidOperationException("清单属于另一字库，请先保存或清空，再切换字库。");
        string? previousLibrary = SelectedLibraryId;
        bool replace = _replace.Checked;
        Reload(selectedLibrary);
        if (SelectedLibraryId == previousLibrary) _replace.Checked = replace;
    }

    /// <summary>当前可编辑候选表；切换图像不清除独立暂存列表。</summary>
    public DataGridView Candidates => _grid;

    /// <summary>跨图保留、已经复核但尚未保存的独立图块数量。</summary>
    public int PendingCount => _draft.Pending.Count;

    /// <summary>当前图草稿是否仍包含未暂存或未保存的无标签/缺失字符。</summary>
    public bool HasUnstagedCandidates =>
        _draft.Candidates.Any(c =>
            c.Label.Length == 0
            || (
                !_draft.Pending.Any(p => p.Character == c.Label)
                && !(_library?.Glyphs.ContainsKey(c.Label) ?? false)
            )
        );

    /// <summary>连接宿主拥有的服务。</summary>
    /// <param name = "manager">宿主拥有的字库管理器。</param>
    /// <param name = "extractor">可选候选提取服务，null时仍允许手动制作。</param>
    /// <param name = "selectedLibrary">可选的初始选中字库标识。</param>
    public void AttachServices(
        IGlyphLibraryManager manager,
        IGlyphCandidateService? extractor = null,
        string? selectedLibrary = null
    )
    {
        Idle();
        if (_draft.Pending.Count > 0)
        {
            throw new InvalidOperationException("请先处理待入库清单。");
        }

        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _extractor = extractor;
        SetBusy(false);
        Reload(selectedLibrary);
    }

    /// <summary>加载源图，仅清除当前候选表；已明确暂存的图块继续保留。</summary>
    /// <param name = "image">新的不可变源图，不会清除跨图暂存。</param>
    public void SetImage(PixelSnapshot image)
    {
        LoadImage(image, "图片");
    }

    /// <summary>把检测结果单字作为未确认的制库草稿；不自动暂存或发布。</summary>
    /// <param name="image">独立候选图块。</param>
    /// <param name="character">候选标签，仍需用户核对。</param>
    public void SetCandidate(PixelSnapshot image, string character)
    {
        LoadImage(image, "检测单字候选");
        AddManualCandidate(new PixelRect(0, 0, image.Width, image.Height));
        _draft.SetLabel(Selected().Id, character);
        RefreshCandidates(Selected().Id);
        Say("已载入单字候选。请核对标签和外观、勾选后加入清单，再保存新版本。");
    }

    /// <summary>把当前字库已有单字载入制库草稿；保留二值化模式，通过显式暂存/保存发布替换修订。</summary>
    /// <param name="character">当前目标字库中要编辑的单字身份。</param>
    public void EditStoredGlyph(string character)
    {
        Idle();
        var head = Head();
        var library = Manager.Load(head.Id, head.Revision);
        if (!library.Glyphs.TryGetValue(character, out var glyph))
            throw new ArgumentException("当前字库中没有该单字。", nameof(character));
        SetCandidate(glyph.Image, glyph.Character);
        _binarization.SelectedItem = glyph.Binarization;
        _replace.Checked = true;
        Say("已载入库内单字供编辑，尚未修改字库。核对后加入清单并保存新修订。");
    }

    /// <summary>加载源图及简短显示名；来源记录保留名称但不保留目录路径。</summary>
    /// <param name = "image">新的不可变源图。</param>
    /// <param name = "sourceName">用于显示和溯源的简短来源名称，不包含目录路径。</param>
    public void SetImage(PixelSnapshot image, string sourceName)
    {
        LoadImage(image, Path.GetFileName(sourceName));
    }

    private void LoadImage(PixelSnapshot image, string name)
    {
        Idle();
        _draft.LoadImage(image, name);
        _image = image;
        _currentName = name;
        _selectedRegionId = null;
        _result = null;
        _text.Clear();
        _sourceName.Text = name;
        _binarization.SelectedItem = "otsu";
        _viewer.SetImage(image);
        _mode.SelectedIndex = 0;
        RefreshCandidates();
        Say(
            "已导入 " + name + "。框行自动分割，或直接补画字块；右侧保留 " + PendingCount + " 个待入库字符。"
        );
    }

    /// <summary>增加并选择单行ROI；同图其他ROI候选和跨图暂存均保留，相同范围选回已有ROI。</summary>
    /// <param name = "bounds">新单行区域的原图整数范围。</param>
    public void SetRegion(PixelRect bounds)
    {
        Idle();
        if (_image == null || !bounds.Fits(_image))
        {
            throw new ArgumentException("ROI超出原图。");
        }

        var region = _draft.AddRegion(bounds);
        RefreshRegions(region.Id);
        Say(
            region.Name
                + "已框选，本图共"
                + Regions.Count
                + "个ROI。可继续框其他行、提取当前ROI或全部ROI，已有候选保留。"
        );
    }

    /// <summary>无论OCR是否可用，都可添加用户绘制的裁剪；初始无标签且未勾选。</summary>
    /// <param name = "bounds">用户明确绘制的原图裁剪范围。</param>
    public void AddManualCandidate(PixelRect bounds)
    {
        Idle();
        if (_mode.SelectedIndex == 0)
        {
            _mode.SelectedIndex = 1;
        }

        var region = SelectedRegion;
        string? owner =
            region != null
            && bounds.X >= region.Bounds.X
            && bounds.Y >= region.Bounds.Y
            && (long)bounds.X + bounds.Width <= (long)region.Bounds.X + region.Bounds.Width
            && (long)bounds.Y + bounds.Height <= (long)region.Bounds.Y + region.Bounds.Height
                ? region.Id
                : null;
        string id = _draft.AddManual(bounds, "", owner);
        RefreshCandidates(id);
        Say("已补画字块。请填写单字；若含两个粘连字，切换“手动切开”后点击分界。");
    }

    /// <summary>按原图X坐标切分表格选中的图块，保留像素并清空两侧标签。</summary>
    /// <param name = "originalX">图块内部的原图X切分坐标，不是缩放后的屏幕坐标。</param>
    public void SplitSelected(int originalX)
    {
        Idle();
        _draft.Split(Selected().Id, originalX);
        _cutGuide = null;
        RefreshCandidates();
        Say("已手动分为两块（不擦墨迹）。请分别填写字符并核对切线；可撤销重切。");
    }

    /// <summary>提取选中ROI，成功时仅替换该ROI候选，失败/取消不清掉已有候选。</summary>
    /// <param name="confirmedText">明确人工文字；null使用OCR，不改写原始读数。</param>
    public Task ExtractAsync(string? confirmedText = null)
    {
        Idle();
        var region = SelectedRegion ?? throw new InvalidOperationException("先导入图片并框选/选择一行文字。");
        return StartExtraction(new[] { region }, false, confirmedText);
    }

    /// <summary>按本图ROI顺序串行提取；各ROI有人工文字时采用人工文字，否则OCR。失败继续下一ROI，取消保留已完成及原有结果。</summary>
    public Task ExtractAllAsync()
    {
        Idle();
        if (_draft.Regions.Count == 0)
            throw new InvalidOperationException("请先在当前图框选文字行ROI。");
        return StartExtraction(_draft.Regions.ToArray(), true, null);
    }

    /// <summary>选回已存在的ROI，恢复该ROI人工文字及最近提取证据，不清候选。</summary>
    /// <param name="id">当前图中的ROI标识。</param>
    public void SelectRegion(string id)
    {
        Idle();
        if (!_draft.Regions.Any(r => r.Id == id))
            throw new ArgumentException("请选择当前图中的ROI。");
        RefreshRegions(id);
    }

    private Task StartExtraction(IReadOnlyList<GlyphDraftRegion> jobs, bool all, string? text)
    {
        if (_image == null || _extractor == null)
            throw new InvalidOperationException("请先载入图片并连接提取服务；无服务时仍可手工补画字块。");
        _grid.EndEdit();
        _cancel = new CancellationTokenSource();
        SetBusy(true);
        _work = ExtractCoreAsync(_image, jobs, all, text, _cancel.Token);
        return _work;
    }

    private async Task ExtractCoreAsync(
        PixelSnapshot image,
        IReadOnlyList<GlyphDraftRegion> jobs,
        bool all,
        string? singleText,
        CancellationToken token
    )
    {
        int completed = 0,
            failed = 0;
        try
        {
            using var source = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToVision(image);
            foreach (var region in jobs)
            {
                token.ThrowIfCancellationRequested();
                Say(
                    "正在提取 "
                        + region.Name
                        + "（"
                        + (completed + failed + 1)
                        + "/"
                        + jobs.Count
                        + "）；其他ROI候选与清单保留，可取消。"
                );
                try
                {
                    var result = await _extractor!.ExtractGlyphCandidatesAsync(
                        source,
                        region.Bounds,
                        all ? region.ConfirmedText : singleText,
                        token
                    );
                    token.ThrowIfCancellationRequested();
                    _draft.ApplyRegionExtraction(region.Id, result);
                    if (result.Segmentation.Characters.Count == 0)
                        failed++;
                    else
                        completed++;
                    if (_selectedRegionId == region.Id)
                        _result = result;
                    _mode.SelectedIndex = _draft.Candidates.Count > 0 ? 2 : 1;
                    RefreshCandidates();
                    if (!all)
                        Say(
                            region.Name
                                + " · "
                                + (
                                    result.Recognition == null
                                        ? "人工文字"
                                        : "OCR：" + result.Recognition.Text
                                )
                                + "；"
                                + result.Segmentation.Reason
                                + "；当前图累计"
                                + _draft.Candidates.Count
                                + "个候选。其他ROI/清单保留。",
                            result.Segmentation.Characters.Count == 0
                        );
                }
                catch (OperationCanceledException)
                {
                    _draft.RecordRegionFailure(region.Id, "已取消；原候选保留。");
                    if (!IsDisposed)
                        RefreshRegions();
                    throw;
                }
                catch (Exception error)
                {
                    _draft.RecordRegionFailure(region.Id, error.Message);
                    failed++;
                    if (!IsDisposed)
                        RefreshRegions();
                    if (!all)
                        throw;
                }
            }
            if (all)
                Say(
                    "本图"
                        + jobs.Count
                        + "个ROI提取结束：成功"
                        + completed
                        + "，失败/无候选"
                        + failed
                        + "。累计"
                        + _draft.Candidates.Count
                        + "个候选；失败区域的旧候选与清单保留。",
                    failed > 0
                );
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            if (!IsDisposed)
                SetBusy(false);
        }
    }

    private async Task RunFromUiAsync(bool manual)
    {
        try
        {
            if (
                _draft.Candidates.Any(c => c.RegionId == _selectedRegionId)
                && MessageBox.Show(
                    this,
                    "重新提取只替换当前ROI的候选和手工调整，其他ROI及右侧清单不变。继续？",
                    "重新分割",
                    MessageBoxButtons.YesNo
                ) != DialogResult.Yes
            )
            {
                return;
            }

            await ExtractAsync(manual ? _text.Text : null);
        }
        catch (OperationCanceledException)
        {
            Say("已取消；已完成ROI、原候选和清单保留。");
        }
        catch (Exception error)
        {
            Say(error.Message, true);
        }
    }

    /// <summary>暂存勾选行的当前标签与像素；识别后可直接编辑再加入，不要求额外字符确认。默认跳过已有或待保存的重复字符。</summary>
    public void StageSelected()
    {
        Idle();
        _grid.EndEdit();
        var head = Head();
        if (_library == null)
        {
            throw new InvalidOperationException("字库尚未成功加载，请刷新后重试。");
        }

        var selected = _grid
            .Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["Use"].Value is bool used && used)
            .Select(r => ((GlyphDraftCandidate)r.Tag!).Id)
            .ToArray();
        int before = PendingCount;
        var skipped = _draft.Stage(head.Id, selected, _library!.Glyphs.Keys,
            (string)_binarization.SelectedItem!, _replace.Checked);
        foreach (var item in _draft.Pending)
        {
            if (!_pendingNames.ContainsKey(item.Character))
            {
                var candidate = _draft.Candidates.FirstOrDefault(c =>
                    c.Label == item.Character && selected.Contains(c.Id)
                );
                var region = _draft.Regions.FirstOrDefault(r => r.Id == candidate?.RegionId);
                _pendingNames[item.Character] =
                    _currentName + (region == null ? " · 手工裁图" : " · " + region.Name);
            }
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells["Use"].Value = false;
        }

        RefreshPending();
        UpdateRowDetails();
        Say(
            "已加入清单 "
                + (PendingCount - before)
                + " 个，累计 "
                + PendingCount
                + " 个。"
                + (skipped.Count > 0 ? "跳过已收录/已暂存字符：" + string.Join("、", skipped) + "。" : "")
                + "现在可导入下一张图继续补字，或点击④保存。尚未写入字库。"
        );
    }

    /// <summary>将复核后的跨图列表一次性发布；失败时保留以便重试，配方仍固定原版本。</summary>
    public int SavePending()
    {
        Idle();
        var head = Head();
        if (!_replace.Checked && _library != null)
        {
            var conflicts = _draft
                .Pending.Where(p => _library.Glyphs.ContainsKey(p.Character))
                .Select(p => p.Character)
                .ToArray();
            if (conflicts.Length > 0)
            {
                throw new InvalidOperationException(
                    "清单中的 "
                        + string.Join("、", conflicts)
                        + " 已在字库中。仅新增请移除这些项；确需替换请勾选“本次允许替换”。清单未丢失。"
                );
            }
        }

        int revision;
        try
        {
            revision = _draft.Publish(
                Manager as IGlyphBatchLibraryManager
                    ?? throw new NotSupportedException("字库未提供批量保存能力。"),
                head.Id,
                head.Revision,
                _replace.Checked
            );
        }
        catch (InvalidOperationException error)
            when (error.Message.StartsWith("Stale library edit", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "字库已被其他操作更新；清单完整保留。请点击“刷新字库”，检查重名项后重试。",
                error
            );
        }

        _pendingNames.Clear();
        RefreshPending();
        Reload(head.Id);
        UpdateRowDetails();
        Say(
            "保存成功：新增版本 r"
                + revision
                + "，当前已收录 "
                + _library!.Glyphs.Count
                + " 个字。可继续导入其他图片补字；配方不会自动改绑。"
        );
        return revision;
    }

    /// <summary>便捷组合入口：先暂存选中的缺失字符，再发布暂存列表。</summary>
    public int SaveSelected()
    {
        StageSelected();
        if (PendingCount == 0)
        {
            return Head().Revision;
        }

        return SavePending();
    }

    /// <summary>关闭前取消并等待借用的原生服务调用完成。</summary>
    public async Task CancelAndWaitAsync()
    {
        _cancel?.Cancel();
        if (_work != null)
        {
            try
            {
                await _work;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    Say(error.Message, true);
                }
            }
        }
    }

    private void ImportImage()
    {
        if (
            HasUnstagedCandidates
            && MessageBox.Show(
                this,
                "换图会清除当前图候选；右侧清单保留。需要的字是否已加入清单？",
                "导入下一张",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            ) != DialogResult.Yes
        )
        {
            return;
        }

        using var dialog = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg" };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        using var image = Image.FromFile(dialog.FileName);
        LoadImage(DrawingImageConverter.FromImage(image), Path.GetFileName(dialog.FileName));
    }

    private void RefreshRegions(string? selectedId = null, bool updateText = true)
    {
        selectedId = selectedId ?? _selectedRegionId;
        var selected =
            _draft.Regions.FirstOrDefault(r => r.Id == selectedId) ?? _draft.Regions.FirstOrDefault();
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            _regionsBox.Items.Clear();
            foreach (var region in _draft.Regions)
                _regionsBox.Items.Add(region);
            _regionsBox.SelectedItem = selected;
            _selectedRegionId = selected?.Id;
            _result = selected?.Extraction;
            if (updateText)
                _text.Text = selected?.DisplayText ?? "";
        }
        finally
        {
            _syncing = syncing;
        }
        UpdateViewer();
    }

    private bool ConfirmReplacing(bool hasCandidates, string message) =>
        !hasCandidates
        || MessageBox.Show(this, message, "制作ROI调整", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            == DialogResult.Yes;

    private void RemoveCurrentRegion()
    {
        var region = SelectedRegion ?? throw new InvalidOperationException("请选择本图文字ROI。");
        if (
            !ConfirmReplacing(
                _draft.Candidates.Any(c => c.RegionId == region.Id),
                "删除" + region.Name + "及其未暂存候选？其他ROI和右侧清单保留。"
            )
        )
            return;
        _draft.RemoveRegion(region.Id);
        RefreshCandidates();
    }

    private void EditRegionBounds()
    {
        var region = SelectedRegion ?? throw new InvalidOperationException("请选择本图文字ROI。");
        var b = region.Bounds;
        string? value = EditorDialogs.Ask(
            region.Name + "原图坐标：X,Y,宽,高",
            $"{b.X},{b.Y},{b.Width},{b.Height}"
        );
        if (value == null)
            return;
        var parts = value
            .Split(',', '，')
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
            .ToArray();
        if (parts.Length != 4)
            throw new ArgumentException("请输入4个整数。");
        if (
            !ConfirmReplacing(
                _draft.Candidates.Any(c => c.RegionId == region.Id),
                "调整该ROI将清除它的旧候选；其他ROI和已暂存清单保留。继续？"
            )
        )
            return;
        _draft.SetRegionBounds(region.Id, new PixelRect(parts[0], parts[1], parts[2], parts[3]));
        RefreshCandidates();
    }

    private async Task RunAllFromUiAsync()
    {
        try
        {
            Idle();
            if (
                !ConfirmReplacing(
                    _draft.Candidates.Any(c => c.RegionId != null),
                    "批量重提取将逐ROI替换成功区域的旧候选/手工调整；失败区域、独立裁图及清单保留。继续？"
                )
            )
                return;
            await ExtractAllAsync();
        }
        catch (OperationCanceledException)
        {
            Say("已取消；已完成ROI、原候选和清单保留。");
        }
        catch (Exception error)
        {
            Say(error.Message, true);
        }
    }

    private GlyphDraftCandidate Selected()
    {
        return _grid.CurrentRow?.Tag as GlyphDraftCandidate
            ?? throw new InvalidOperationException("先在候选表中选中一个字块。");
    }

    private void EditBounds()
    {
        var c = Selected();
        var b = c.Bounds;
        string? value = EditorDialogs.Ask(
            "原图坐标：X,Y,宽,高（调整后须重新核对）",
            $"{b.X},{b.Y},{b.Width},{b.Height}"
        );
        if (value == null)
        {
            return;
        }

        var parts = value
            .Split(',', '，')
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
            .ToArray();
        if (parts.Length != 4)
        {
            throw new ArgumentException("请输入4个整数，以逗号分隔。");
        }

        _draft.Resize(c.Id, new PixelRect(parts[0], parts[1], parts[2], parts[3]));
        RefreshCandidates(c.Id);
    }

    private void SelectCandidates(bool missing)
    {
        _grid.EndEdit();
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var c = (GlyphDraftCandidate)row.Tag!;
            row.Cells["Use"].Value =
                DP.Vision.Algorithms.CharacterIdentity.IsGlyph(c.Label)
                && c.Image.Width >= 4
                && c.Image.Height >= 4
                && chosen.Add(c.Label)
                && (!missing || !(_library?.Glyphs.ContainsKey(c.Label) ?? false))
                && !_draft.Pending.Any(p => p.Character == c.Label);
        }
    }

    private void RefreshCandidates(string? selectId = null)
    {
        var chosen = new HashSet<string>(
            _grid
                .Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells["Use"].Value is bool used && used && r.Tag is GlyphDraftCandidate)
                .Select(r => ((GlyphDraftCandidate)r.Tag!).Id),
            StringComparer.Ordinal
        );
        selectId = selectId ?? (_grid.CurrentRow?.Tag as GlyphDraftCandidate)?.Id;
        _syncing = true;
        try
        {
            DisposeRows(_grid);
            _grid.Rows.Clear();
            foreach (var candidate in _draft.Candidates)
            {
                int i = _grid.Rows.Add(
                    chosen.Contains(candidate.Id),
                    DrawingImageConverter.ToBitmap(candidate.Image),
                    candidate.Label,
                    ""
                );
                _grid.Rows[i].Tag = candidate;
                _grid.Rows[i].DefaultCellStyle.BackColor = Color.LemonChiffon;
            }

            if (selectId != null)
            {
                var row = _grid
                    .Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(r => ((GlyphDraftCandidate)r.Tag!).Id == selectId);
                if (row != null)
                {
                    _grid.CurrentCell = row.Cells["Character"];
                }
            }
        }
        finally
        {
            _syncing = false;
        }

        RefreshRegions();
        UpdateRowDetails();
        UpdateViewer();
    }

    private void UpdateRowDetails()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var c = (GlyphDraftCandidate)row.Tag!;
            string state =
                c.Label.Length == 0 ? "需填单字"
                : _draft.Pending.Any(p => p.Character == c.Label) ? "已暂存（调整不改清单）"
                : _library?.Glyphs.ContainsKey(c.Label) == true ? "库中已有（默认跳过）"
                : "库中缺字";
            var region = _draft.Regions.FirstOrDefault(r => r.Id == c.RegionId);
            row.Cells["Details"].Value =
                (
                    region == null
                        ? "独立手工裁图"
                        : region.Name + (region.Error == null ? "" : "（提取失败，保留旧候选）")
                )
                + " · "
                + state
                + " · "
                + (
                    c.Operation.StartsWith("manual_", StringComparison.Ordinal) ? "人工调整需复核"
                    : c.Operation == "thin_bridge_vertical_candidates" ? "薄粘连切线须复核"
                    : "自动候选需复核"
                )
                + " · "
                + c.Bounds;
        }

        UpdateCoverageText();
    }

    private void UpdateViewer()
    {
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            _viewer.SetCharacters(Array.Empty<CharacterPatch>());
            bool lines = _mode.SelectedIndex == 0 || _mode.SelectedIndex == 4;
            var boxes = lines
                ? _draft
                    .Regions.Select(r => new InspectionRegion(r.Name, ERegionKind.Text, r.Bounds))
                    .ToArray()
                : _draft
                    .Candidates.Select(
                        (c, i) =>
                            new InspectionRegion(
                                (i + 1) + " · " + (c.Label.Length == 0 ? "未标字" : c.Label),
                                ERegionKind.Text,
                                c.Bounds
                            )
                    )
                    .ToArray();
            _viewer.SetOverlays(boxes, Array.Empty<InspectionFinding>());
            if (lines)
                _viewer.SelectedRegionIndex = _draft
                    .Regions.ToList()
                    .FindIndex(r => r.Id == _selectedRegionId);
        }
        finally
        {
            _syncing = syncing;
        }
    }

    private void UpdateMode()
    {
        _cutGuide = null;
        _viewer.EditRegions = _mode.SelectedIndex == 2 || _mode.SelectedIndex == 4;
        _viewer.AllowRegionDrawing = _mode.SelectedIndex != 3;
        _viewer.Cursor =
            _mode.SelectedIndex == 1 || _mode.SelectedIndex == 3 ? Cursors.Cross : Cursors.Default;
        UpdateViewer();
        _viewer.Invalidate();
    }

    private void RefreshPending()
    {
        DisposeRows(_pending);
        _pending.Rows.Clear();
        foreach (var item in _draft.Pending)
        {
            int i = _pending.Rows.Add(
                DrawingImageConverter.ToBitmap(item.Image),
                item.Character,
                _pendingNames.TryGetValue(item.Character, out var name) ? name : "图片"
            );
            _pending.Rows[i].Tag = item;
        }

        _pendingTitle.Text = "④ 待入库清单（" + PendingCount + "） · 换图保留";
    }

    private void LibraryChanged()
    {
        if (_syncing)
        {
            return;
        }

        var head = _libraries.SelectedItem as GlyphLibraryInfo;
        if (_draft.PendingLibraryId != null && head?.Id != _draft.PendingLibraryId)
        {
            _syncing = true;
            var owned = _libraries
                .Items.Cast<GlyphLibraryInfo>()
                .FirstOrDefault(h => h.Id == _draft.PendingLibraryId);
            _libraries.SelectedItem = owned;
            _syncing = false;
            if (owned == null)
            {
                _library = null;
                UpdateCoverage();
            }

            Say(
                owned == null
                    ? "待入库的目标字库已不可用或已归档，清单仍保留。请恢复目标字库后刷新，或明确清空清单。"
                    : "清单属于当前字库。请先保存或清空，再切换字库。",
                true
            );
            return;
        }

        _replace.Checked = false;
        _library = null;
        _library = head == null ? null : Manager.Load(head.Id, head.Revision);
        UpdateCoverage();
        UpdateRowDetails();
    }

    private void UpdateCoverageText()
    {
        var keys =
            _library?.Glyphs.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        var current = _draft
            .Candidates.Select(c => c.Label)
            .Where(DP.Vision.Algorithms.CharacterIdentity.IsGlyph)
            .Distinct(StringComparer.Ordinal);
        _coverage.Text =
            _library == null
                ? "尚无字库：点击左上角“＋ 新建字库”。支持中文、字母、数字、标点和符号。"
                : "当前 r"
                    + _library.Revision
                    + " · 已收录 "
                    + keys.Length
                    + " 个单字（支持中文/标点，无需收齐）\r\n已有："
                    + string.Join(" ", keys)
                    + "\r\n当前图候选缺字："
                    + string.Join(" ", current.Where(c => !_library.Glyphs.ContainsKey(c)))
                    + "\r\n容量上限4096字/1600万参考像素，不是必需补齐的字表。";
    }

    private void UpdateCoverage()
    {
        UpdateCoverageText();
        var keys =
            _library?.Glyphs.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        _saved.Items.Clear();
        foreach (var key in keys)
        {
            _saved.Items.Add(key);
        }

        var old = _savedPreview.Image;
        _savedPreview.Image = null;
        old?.Dispose();
        if (keys.Length > 0)
        {
            _saved.SelectedIndex = 0;
        }
    }

    private void Reload(string? id = null)
    {
        id = id ?? (_libraries.SelectedItem as GlyphLibraryInfo)?.Id;
        var heads = Manager.ListLibraries(false);
        _syncing = true;
        _libraries.Items.Clear();
        foreach (var head in heads)
        {
            _libraries.Items.Add(head);
        }

        if (heads.Count > 0)
        {
            _libraries.SelectedItem = heads.FirstOrDefault(h => h.Id == id) ?? heads[0];
        }

        _syncing = false;
        LibraryChanged();
    }

    private GlyphLibraryInfo Head()
    {
        return _libraries.SelectedItem as GlyphLibraryInfo
            ?? throw new InvalidOperationException(
                "尚未选择字库。请先点击左上角“＋ 新建字库”，输入字体/类别名称。"
            );
    }

    private IGlyphLibraryManager Manager =>
        _manager ?? throw new InvalidOperationException("未连接字库管理器。");

    private void SetBusy(bool busy)
    {
        _tools.Enabled =
            _regionTools.Enabled =
            _editTools.Enabled =
            _reviewTools.Enabled =
            _viewer.Enabled =
            _grid.Enabled =
            _pending.Enabled =
            _text.Enabled =
            _binarization.Enabled =
                !busy;
        _recognize.Enabled = _segment.Enabled = _extractAll.Enabled = !busy && _extractor != null;
        _extractionHint.Text =
            _extractor == null
                ? "自动提取服务未连接：请在配置页重载资源；仍可手工补画单字。"
                : "识别后直接在表格修改单字并加入清单；行文字纠错可选。";
        _cancelButton.Enabled = busy;
    }

    private void Idle()
    {
        if (Environment.CurrentManagedThreadId != _uiThread)
        {
            throw new InvalidOperationException("Use the owning UI thread.");
        }

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GlyphQuickBuilderControl));
        }

        if (IsBusy)
        {
            throw new InvalidOperationException("提取期间不能修改字块或字库，请先取消并等待完成。");
        }
    }

    private void Say(string message, bool error = false)
    {
        _status.ForeColor = error ? ModernTheme.Dark.Error : ModernTheme.Dark.TextSecondary;
        _status.Text = message;
    }

    private void TryUi(Action action)
    {
        try
        {
            Idle();
            action();
        }
        catch (Exception error)
        {
            Say(error.Message, true);
        }
    }

    private void Button(FlowLayoutPanel parent, string text, Action action)
    {
        var button = InspectionUiStyle.CreateButton(text);
        button.Margin = new Padding(3);
        button.Click += (_, __) => TryUi(action);
        parent.Controls.Add(button);
    }

    private static FlowLayoutPanel Bar()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(3),
            WrapContents = true,
        };
    }

    private static DataGridView Table(bool readOnly)
    {
        return new ModernDataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowTemplate = { Height = 65 },
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
        };
    }

    private static void DisposeRows(DataGridView table)
    {
        foreach (DataGridViewRow row in table.Rows)
        {
            (row.Cells["Patch"].Value as Image)?.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _syncing = true;
            _cancel?.Cancel();
            DisposeRows(_grid);
            DisposeRows(_pending);
            _savedPreview.Image?.Dispose();
        }

        base.Dispose(disposing);
    }

    internal static void ShowPage(
        IWin32Window? owner,
        IGlyphLibraryManager manager,
        IGlyphCandidateService? service,
        PixelSnapshot? image = null,
        string? selectedLibrary = null,
        string? candidateCharacter = null
    )
    {
        using var form = new Form
        {
            Text = "字符库 · 同图多ROI / 多图补录 / 人工分割 / 清单保存",
            Width = 1380,
            Height = 960,
            MinimumSize = new Size(1120, 780),
            StartPosition = FormStartPosition.CenterParent,
        };
        using var page = new GlyphQuickBuilderControl();
        page.AttachServices(manager, service, selectedLibrary);
        if (image != null)
        {
            if (candidateCharacter != null) page.SetCandidate(image, candidateCharacter);
            else page.SetImage(image);
        }

        form.Controls.Add(page);
        GuardClose(form, page);
        InspectionUiStyle.Apply(form);
        form.ShowDialog(owner);
    }

    /// <summary>关闭宿主窗口前先取消并等待进行中的识别，并确认是否丢弃未入库的草稿。</summary>
    internal static void GuardClose(Form form, GlyphQuickBuilderControl page)
    {
        bool closing = false;
        form.FormClosing += async (_, e) =>
        {
            if (!closing && page.IsBusy)
            {
                e.Cancel = true;
                await page.CancelAndWaitAsync();
                closing = true;
                form.Close();
                return;
            }

            if (
                (page.PendingCount > 0 || page.HasUnstagedCandidates)
                && MessageBox.Show(
                    form,
                    "还有 "
                        + page.PendingCount
                        + " 个待入库字符"
                        + (page.HasUnstagedCandidates ? "及当前图未暂存候选" : "")
                        + "未保存。关闭会丢弃这些草稿，确定关闭？",
                    "未保存的清单",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                ) != DialogResult.Yes
            )
            {
                e.Cancel = true;
                closing = false;
            }
        };
    }
}
