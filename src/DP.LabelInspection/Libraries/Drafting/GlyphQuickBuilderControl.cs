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

/// <summary>
/// 多图、多ROI单字制库页：上方工具栏，左侧图像（直接选中/调整/Delete删除框），右上字库收录矩阵与可缩放大图，
/// 右下候选表；勾选后直接保存到字库（每次保存为一个新修订），不再经过待入库清单。
/// </summary>
public sealed class GlyphQuickBuilderControl : UserControl
{
    // 编辑模式：文字行ROI / 单字框 / 切开粘连字。选中、移动、缩放始终可用，不需要单独的“调整”模式。
    private const int LinesMode = 0, GlyphsMode = 1, CutMode = 2;
    private const int ThumbnailPixels = 56;
    private readonly GlyphDraftSession _draft = new GlyphDraftSession();
    private readonly ImageViewerControl _viewer = new ImageViewerControl
    {
        Dock = DockStyle.Fill,
        ShowFindingLabels = false,
        EditRegions = true,
        DrawOutsideRegions = true,
    };
    private readonly DataGridView _grid = Table();
    private readonly ModernSelect _libraries = new ModernSelect { Width = 230 };
    private readonly ModernSelect _mode = new ModernSelect { Width = 150 };
    private readonly ModernInput _text = new ModernInput { Width = 180, MaxLength = 256 };
    private readonly ModernSelect _regionsBox = new ModernSelect { Width = 200 };
    private readonly ModernSelect _binarization = new ModernSelect { Width = 110 };
    private readonly ModernToolStrip _libraryBar = new ModernToolStrip { Dock = DockStyle.Top, Theme = ModernTheme.Dark };
    private readonly ModernToolStrip _editBar = new ModernToolStrip { Dock = DockStyle.Top, Theme = ModernTheme.Dark };
    private readonly ToolStripButton _recognize = new ToolStripButton("提取当前ROI（OCR）") { Enabled = false },
        _segment = new ToolStripButton("按文字重分割") { Enabled = false, ToolTipText = "按“行文字纠错”填写的文字重新分割当前ROI" },
        _extractAll = new ToolStripButton("提取全部ROI") { Enabled = false },
        _cancelButton = new ToolStripButton("取消提取") { Enabled = false };
    private readonly Label _status = new Label
    {
        Dock = DockStyle.Bottom,
        Height = 40,
        Padding = new Padding(8),
        BackColor = ModernTheme.Dark.Container,
        Text = "① 选择/新建字库 → ② 导入图片 → ③ 框文字行并提取，或直接画单字框 → ④ 核对单字、勾选后保存到字库。",
    };
    private readonly ToolStripLabel _sourceName = new ToolStripLabel("尚未导入图片");
    private readonly Label _coverageTitle = new Label
    {
        Dock = DockStyle.Top,
        Height = 28,
        Padding = new Padding(6, 6, 6, 0),
        Text = "字库已收录 0 个",
    };
    // 收录矩阵：单字键＋缩略图按网格排布；4096字时也只用一个控件和一个图像列表。
    private readonly ListView _tiles = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.LargeIcon,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly ImageList _thumbnails = new ImageList
    {
        ImageSize = new Size(ThumbnailPixels, ThumbnailPixels),
        ColorDepth = ColorDepth.Depth32Bit,
    };
    private readonly ImageViewerControl _preview = new ImageViewerControl
    {
        Dock = DockStyle.Fill,
        ShowFindingLabels = false,
        AllowRegionDrawing = false,
    };
    private readonly Label _previewCaption = new Label { AutoSize = true, Margin = new Padding(6, 9, 6, 0) };
    private readonly ModernCheckbox _replace = new ModernCheckbox
    {
        Text = "允许替换库中已有字符",
        Width = 220,
        Height = 34,
    };
    private readonly FlowLayoutPanel _reviewTools = Bar();
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

        // 工具栏第一行：字库与图片。
        _libraryBar.Items.Add(new ToolStripLabel("目标字库"));
        _libraryBar.Items.Add(Host(_libraries, "制库目标；不同字体/宽窄/工艺请分别建库"));
        Tool(_libraryBar, "＋ 新建字库", () =>
        {
            var name = EditorDialogs.Ask("字形类别（字体/宽窄/工艺不同请分别建库）", "");
            if (!string.IsNullOrWhiteSpace(name))
                Reload(Manager.CreateLibrary(name!));
        });
        Icon(_libraryBar, ModernIconKind.Refresh, "刷新字库", () => Reload());
        _libraryBar.Items.Add(new ToolStripSeparator());
        Tool(_libraryBar, "导入图片…", ImportImage);
        _libraryBar.Items.Add(_sourceName);
        _libraryBar.Items.Add(new ToolStripSeparator());
        _binarization.Items.AddRange(new object[] { "otsu", "fixed", "midpoint" });
        _binarization.SelectedIndex = 0;
        _libraryBar.Items.Add(new ToolStripLabel("参考二值化"));
        _libraryBar.Items.Add(Host(_binarization, "保存参考时使用的二值化方式"));

        // 工具栏第二行：编辑。
        _mode.Items.AddRange(new object[] { "文字行ROI", "单字框", "切开粘连字" });
        _mode.SelectedIndex = LinesMode;
        _editBar.Items.Add(new ToolStripLabel("编辑"));
        _editBar.Items.Add(Host(_mode, "文字行ROI：拖框新建行、点击选中、拖边调整；单字框：手工补画/调整字块；切开：在选中字块内点击分界"));
        _editBar.Items.Add(new ToolStripSeparator());
        _editBar.Items.Add(new ToolStripLabel("文字行"));
        _editBar.Items.Add(Host(_regionsBox, "本图的文字行ROI"));
        _recognize.Click += async (_, __) => await RunFromUiAsync(false);
        _editBar.Items.Add(_recognize);
        _editBar.Items.Add(new ToolStripLabel("行文字纠错"));
        _editBar.Items.Add(Host(_text, "可选：填写该行实际文字，用于纠正OCR或按文字重分割"));
        _segment.Click += async (_, __) => await RunFromUiAsync(true);
        _editBar.Items.Add(_segment);
        _extractAll.Click += async (_, _) => await RunAllFromUiAsync();
        _editBar.Items.Add(_extractAll);
        _cancelButton.Click += (_, __) => _cancel?.Cancel();
        _editBar.Items.Add(_cancelButton);
        _editBar.Items.Add(new ToolStripSeparator());
        Icon(_editBar, ModernIconKind.Undo, "撤销", () =>
        {
            _draft.Undo();
            RefreshCandidates();
        });
        Icon(_editBar, ModernIconKind.Redo, "重做", () =>
        {
            _draft.Redo();
            RefreshCandidates();
        });
        Icon(_editBar, ModernIconKind.FitWindow, "适应窗口（Home）", () => _viewer.FitToWindow());
        _regionsBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _regionsBox.SelectedItem is GlyphDraftRegion region)
                TryUi(() => SelectRegion(region.Id));
        };

        // 候选表：识别/补画的字块，填写单字后勾选保存。
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "选用", FillWeight = 35 });
        _grid.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Patch",
            HeaderText = "字块",
            ReadOnly = true,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            FillWeight = 60,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Character",
            HeaderText = "单字",
            MaxInputLength = 2,
            FillWeight = 40,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Details",
            HeaderText = "来源 / 状态 / 原图坐标",
            ReadOnly = true,
            FillWeight = 160,
        });
        Button(_reviewTools, "只选库中缺字", () => SelectCandidates(true));
        Button(_reviewTools, "选择有标签项", () => SelectCandidates(false));
        Button(_reviewTools, "取消选择", () =>
        {
            foreach (DataGridViewRow row in _grid.Rows)
                row.Cells["Use"].Value = false;
        });
        _reviewTools.Controls.Add(_replace);
        Button(_reviewTools, "保存选中到字库", () => SaveSelected());

        // 右上：收录矩阵（键＋缩略图）与单击后显示的可缩放大图。
        _tiles.LargeImageList = _thumbnails;
        var previewBar = Bar();
        previewBar.Dock = DockStyle.Bottom;
        previewBar.Controls.Add(_previewCaption);
        Button(previewBar, "编辑此字", () =>
        {
            if (_tiles.SelectedItems.Count != 1 || _tiles.SelectedItems[0].Tag is not string character)
                throw new InvalidOperationException("请先在左侧收录矩阵中单击一个单字。");
            if (ConfirmReplacing(_draft.Candidates.Count > 0, "载入该单字会替换当前图片和候选，继续？"))
                EditStoredGlyph(character);
        });
        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(previewBar);
        var coverageSplit = new ModernSplitter
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            InitialPanel2Size = 0,
            Size = new Size(520, 300),
            SplitterDistance = 300,
        };
        coverageSplit.Panel1.Controls.Add(_tiles);
        coverageSplit.Panel2.Controls.Add(previewPanel);
        var coveragePanel = new Panel { Dock = DockStyle.Fill };
        coveragePanel.Controls.Add(coverageSplit);
        coveragePanel.Controls.Add(_coverageTitle);
        var candidatesPanel = new Panel { Dock = DockStyle.Fill };
        candidatesPanel.Controls.Add(_grid);
        candidatesPanel.Controls.Add(_reviewTools);
        var right = new ModernSplitter
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            InitialPanel2Size = 0,
            Orientation = Orientation.Horizontal,
            Size = new Size(520, 760),
            SplitterDistance = 330,
        };
        right.Panel1.Controls.Add(coveragePanel);
        right.Panel2.Controls.Add(candidatesPanel);
        var columns = new ModernSplitter
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            InitialPanel2Size = 520,
            Size = new Size(1220, 760),
            SplitterDistance = 700,
        };
        // 工具栏只在左侧图像上方；右侧收录矩阵与候选表从窗口顶部开始，不被工具栏遮挡。
        // 停靠按Z序倒序处理：后加入的工具栏在最上方。
        columns.Panel1.Controls.Add(_viewer);
        columns.Panel1.Controls.Add(_editBar);
        columns.Panel1.Controls.Add(_libraryBar);
        columns.Panel2.Controls.Add(right);
        Controls.Add(columns);
        Controls.Add(_status);
        InspectionUiStyle.Apply(this);

        _mode.SelectedIndexChanged += (_, __) =>
        {
            UpdateMode();
            Say(
                _mode.SelectedIndex == CutMode
                    ? "先在候选表或图上选中粘连块，再在图上点击分界切开；两侧须分别填写单字，切线两侧至少各4像素。"
                : _mode.SelectedIndex == GlyphsMode
                    ? "在空白处拖框补画一个字块；点击字块选中，拖边/角调整，Delete删除。调整后须重新核对。"
                : "在空白处拖框新建文字行；点击选中，拖边/角调整，Delete删除。每行独立保留文字和候选。"
            );
        };
        _libraries.SelectedIndexChanged += (_, __) => TryUi(LibraryChanged);
        _tiles.SelectedIndexChanged += (_, __) => ShowStoredGlyph();
        _tiles.ItemActivate += (_, __) => TryUi(() =>
        {
            if (_tiles.SelectedItems.Count == 1 && _tiles.SelectedItems[0].Tag is string character
                && ConfirmReplacing(_draft.Candidates.Count > 0, "载入该单字会替换当前图片和候选，继续？"))
                EditStoredGlyph(character);
        });
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
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_syncing || e.RowIndex < 0)
                return;
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
                if (_mode.SelectedIndex != LinesMode)
                    UpdateViewer();
                _viewer.FocusRegion(candidate.Bounds);
            }
        };
        // 表格中（非编辑单元格时）按Delete删除选中字块。
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode)
            {
                TryUi(RemoveSelectedCandidate);
                e.Handled = true;
            }
        };
        _viewer.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || IsBusy)
                return;
            TryUi(() =>
            {
                // 只删除画布上当前选中的框；点空白取消选中后按Delete不删除任何东西。
                if (_viewer.SelectedRegionIndex < 0)
                    throw new InvalidOperationException("请先点击选中要删除的框。");
                if (_mode.SelectedIndex == LinesMode)
                    RemoveCurrentRegion();
                else
                    RemoveSelectedCandidate();
            });
            e.Handled = true;
        };
        _viewer.RegionDrawn += (_, e) =>
        {
            if (IsBusy)
                return;
            TryUi(() =>
            {
                if (_mode.SelectedIndex == GlyphsMode)
                    AddManualCandidate(e.Bounds);
                else if (_mode.SelectedIndex == LinesMode)
                    SetRegion(e.Bounds);
            });
        };
        _viewer.RegionEdited += (_, e) =>
            TryUi(() =>
            {
                if (_mode.SelectedIndex == LinesMode && e.Index >= 0 && e.Index < _draft.Regions.Count)
                {
                    var region = _draft.Regions[e.Index];
                    try
                    {
                        if (
                            ConfirmReplacing(
                                _draft.Candidates.Any(c => c.RegionId == region.Id),
                                "调整该文字行将清除它的旧候选；其他文字行保留。继续？"
                            )
                        )
                            _draft.SetRegionBounds(region.Id, e.Bounds);
                    }
                    finally
                    {
                        RefreshCandidates();
                    }
                }
                else if (_mode.SelectedIndex == GlyphsMode && e.Index >= 0 && e.Index < _draft.Candidates.Count)
                {
                    string id = _draft.Candidates[e.Index].Id;
                    _draft.Resize(id, e.Bounds);
                    RefreshCandidates(id);
                }
            });
        // 点击即选中：文字行模式同步当前ROI，单字框模式同步候选表当前行。
        _viewer.SelectedRegionChanged += (_, _) =>
        {
            if (_syncing || IsBusy)
                return;
            int index = _viewer.SelectedRegionIndex;
            if (_mode.SelectedIndex == LinesMode && index >= 0 && index < _draft.Regions.Count)
            {
                var region = _draft.Regions[index];
                if (region.Id != _selectedRegionId)
                    TryUi(() => SelectRegion(region.Id));
            }
            else if (_mode.SelectedIndex == GlyphsMode && index >= 0 && index < _grid.Rows.Count)
            {
                _syncing = true;
                try
                {
                    _grid.CurrentCell = _grid.Rows[index].Cells["Character"];
                }
                finally
                {
                    _syncing = false;
                }
            }
        };
        _viewer.MouseDown += (_, e) =>
        {
            if (
                !IsBusy
                && _mode.SelectedIndex == CutMode
                && e.Button == MouseButtons.Left
                && _viewer.ClientToImage(e.Location) is Point point
            )
            {
                TryUi(() =>
                {
                    var selected = Selected();
                    if (point.Y < selected.Bounds.Y || point.Y >= selected.Bounds.Y + selected.Bounds.Height)
                        throw new ArgumentException("请在选中字块内点击切线。");
                    SplitSelected(point.X);
                });
            }
        };
        _viewer.MouseMove += (_, e) =>
        {
            if (_mode.SelectedIndex == CutMode && !IsBusy)
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
                _mode.SelectedIndex != CutMode
                || !_cutGuide.HasValue
                || _grid.CurrentRow?.Tag is not GlyphDraftCandidate c
            )
                return;
            var p = _cutGuide.Value;
            var box = c.Bounds;
            if (p.X < box.X || p.X > box.X + box.Width || p.Y < box.Y || p.Y > box.Y + box.Height)
                return;
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

    /// <summary>刷新最新字库并选择目标，不清除当前图候选。</summary>
    /// <param name="selectedLibrary">要选择的类别标识；null保留当前目标。</param>
    public void RefreshLibraries(string? selectedLibrary = null)
    {
        Idle();
        string? previousLibrary = SelectedLibraryId;
        bool replace = _replace.Checked;
        Reload(selectedLibrary);
        if (SelectedLibraryId == previousLibrary) _replace.Checked = replace;
    }

    /// <summary>当前图的可编辑候选表。</summary>
    public DataGridView Candidates => _grid;

    /// <summary>当前图是否还有未保存到字库的候选（无标签或库中缺少该字）。</summary>
    public bool HasUnsavedCandidates =>
        _draft.Candidates.Any(c => c.Label.Length == 0 || !(_library?.Glyphs.ContainsKey(c.Label) ?? false));

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
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _extractor = extractor;
        SetBusy(false);
        Reload(selectedLibrary);
    }

    /// <summary>加载源图并清除当前候选表；已保存到字库的单字不受影响。</summary>
    /// <param name = "image">新的不可变源图。</param>
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
        Say("已载入单字候选。请核对标签和外观，勾选后点“保存选中到字库”。");
    }

    /// <summary>把当前字库已有单字载入制库草稿；保留二值化模式，保存时发布替换修订。</summary>
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
        Say("已载入库内单字供编辑，尚未修改字库。核对后勾选并保存（已勾选“允许替换”）。");
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
        _mode.SelectedIndex = LinesMode;
        RefreshCandidates();
        Say("已导入 " + name + "。拖框选文字行后提取，或切换“单字框”直接补画字块。");
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
        if (_mode.SelectedIndex == LinesMode)
            _mode.SelectedIndex = GlyphsMode;

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
        Say("已补画字块。请填写单字；若含两个粘连字，切换“切开粘连字”后点击分界。");
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
                        + "）；其他ROI候选保留，可取消。"
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
                    _mode.SelectedIndex = GlyphsMode;
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
                                + "个候选，其他ROI保留。",
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
                        + "个候选；失败区域的旧候选保留。",
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
                    "重新提取只替换当前ROI的候选和手工调整，其他ROI不变。继续？",
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
            Say("已取消；已完成ROI和原候选保留。");
        }
        catch (Exception error)
        {
            Say(error.Message, true);
        }
    }

    /// <summary>
    /// 把勾选且已填写单字的候选直接保存到当前字库，发布为一个新修订。默认跳过库中已有字符；
    /// 勾选“允许替换库中已有字符”时替换。失败时字库不变，候选和勾选保留以便修改后重试。
    /// </summary>
    /// <returns>保存后的字库修订；没有可保存的新字符时为当前修订。</returns>
    public int SaveSelected()
    {
        Idle();
        _grid.EndEdit();
        var head = Head();
        if (_library == null)
            throw new InvalidOperationException("字库尚未成功加载，请刷新后重试。");
        var selected = _grid
            .Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["Use"].Value is bool used && used)
            .Select(r => ((GlyphDraftCandidate)r.Tag!).Id)
            .ToArray();
        // 草稿会话的暂存只在本次保存内使用：暂存后立即发布，任何失败都清掉暂存，不留跨图清单。
        _draft.ClearPending();
        int revision;
        IReadOnlyList<string> skipped;
        try
        {
            skipped = _draft.Stage(head.Id, selected, _library.Glyphs.Keys,
                (string)_binarization.SelectedItem!, _replace.Checked);
            if (_draft.Pending.Count == 0)
            {
                Say("没有可保存的新字符：" + string.Join("、", skipped) + " 已在字库中。需要替换请勾选“允许替换库中已有字符”。", true);
                return head.Revision;
            }

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
            throw new InvalidOperationException("字库已被其他操作更新，本次未保存。请点击“刷新字库”后重试。", error);
        }
        finally
        {
            _draft.ClearPending();
        }

        foreach (DataGridViewRow row in _grid.Rows)
            row.Cells["Use"].Value = false;
        Reload(head.Id);
        UpdateRowDetails();
        Say(
            "已保存到字库：新修订 r"
                + revision
                + "，当前已收录 "
                + _library!.Glyphs.Count
                + " 个字。"
                + (skipped.Count > 0 ? "已在库中而跳过：" + string.Join("、", skipped) + "。" : "")
                + "配方不会自动改绑新修订。"
        );
        return revision;
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
            HasUnsavedCandidates
            && MessageBox.Show(
                this,
                "换图会清除当前图的候选。需要的字是否已保存到字库？",
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
                "删除" + region.Name + "及其候选？其他ROI保留。"
            )
        )
            return;
        _draft.RemoveRegion(region.Id);
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
                    "批量重提取将逐ROI替换成功区域的旧候选/手工调整；失败区域和独立字块保留。继续？"
                )
            )
                return;
            await ExtractAllAsync();
        }
        catch (OperationCanceledException)
        {
            Say("已取消；已完成ROI和原候选保留。");
        }
        catch (Exception error)
        {
            Say(error.Message, true);
        }
    }

    private GlyphDraftCandidate Selected()
    {
        return _grid.CurrentRow?.Tag as GlyphDraftCandidate
            ?? throw new InvalidOperationException("先在候选表或图上选中一个字块。");
    }

    private void RemoveSelectedCandidate()
    {
        var candidate = Selected();
        _draft.Remove(candidate.Id);
        RefreshCandidates();
        Say("已删除字块" + (candidate.Label.Length == 0 ? "" : "“" + candidate.Label + "”") + "；可撤销。");
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
                && (!missing || !(_library?.Glyphs.ContainsKey(c.Label) ?? false));
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

        UpdateCoverageTitle();
    }

    private void UpdateViewer()
    {
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            _viewer.SetCharacters(Array.Empty<CharacterPatch>());
            bool lines = _mode.SelectedIndex == LinesMode;
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
            _viewer.SelectedRegionIndex = lines
                ? _draft.Regions.ToList().FindIndex(r => r.Id == _selectedRegionId)
                : _grid.CurrentRow?.Tag is GlyphDraftCandidate current
                    ? _draft.Candidates.ToList().FindIndex(c => c.Id == current.Id)
                    : -1;
        }
        finally
        {
            _syncing = syncing;
        }
    }

    private void UpdateMode()
    {
        _cutGuide = null;
        // 选中/移动/缩放始终可用，在空白处拖动新建；切开模式只接受点击。
        _viewer.EditRegions = _mode.SelectedIndex != CutMode;
        _viewer.AllowRegionDrawing = _mode.SelectedIndex != CutMode;
        _viewer.Cursor = _mode.SelectedIndex == CutMode ? Cursors.Cross : Cursors.Default;
        UpdateViewer();
        _viewer.Invalidate();
    }

    private void LibraryChanged()
    {
        if (_syncing)
        {
            return;
        }

        var head = _libraries.SelectedItem as GlyphLibraryInfo;
        _replace.Checked = false;
        _library = null;
        _library = head == null ? null : Manager.Load(head.Id, head.Revision);
        UpdateCoverage();
        UpdateRowDetails();
    }

    private void UpdateCoverageTitle()
    {
        _coverageTitle.Text = _library == null
            ? "尚无字库：点击“＋ 新建字库”"
            : "字库已收录 " + _library.Glyphs.Count + " 个（r" + _library.Revision + "）";
    }

    /// <summary>重建收录矩阵：每个单字一个缩略图块，按Unicode顺序排布。</summary>
    private void UpdateCoverage()
    {
        UpdateCoverageTitle();
        string? selected = _tiles.SelectedItems.Count == 1 ? _tiles.SelectedItems[0].Tag as string : null;
        _tiles.BeginUpdate();
        try
        {
            _tiles.Items.Clear();
            var old = _thumbnails.Images.Cast<Image>().ToArray();
            _thumbnails.Images.Clear();
            foreach (var image in old)
                image.Dispose();
            if (_library != null)
            {
                foreach (var key in _library.Glyphs.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    _thumbnails.Images.Add(key, Thumbnail(_library.Glyphs[key].Image));
                    _tiles.Items.Add(new ListViewItem(key, key) { Tag = key, ToolTipText = key });
                }
            }
        }
        finally
        {
            _tiles.EndUpdate();
        }

        var item = _tiles.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == selected)
            ?? (_tiles.Items.Count > 0 ? _tiles.Items[0] : null);
        if (item != null)
            item.Selected = true;
        ShowStoredGlyph();
    }

    /// <summary>右侧大图显示收录矩阵中单击的单字，可滚轮缩放、中键/右键平移、Home复位。</summary>
    private void ShowStoredGlyph()
    {
        if (_library != null && _tiles.SelectedItems.Count == 1 && _tiles.SelectedItems[0].Tag is string key
            && _library.Glyphs.TryGetValue(key, out var glyph))
        {
            _preview.SetImage(glyph.Image);
            _preview.FitToWindow();
            _previewCaption.Text = "“" + key + "” " + glyph.Image.Width + "×" + glyph.Image.Height + " · " + glyph.Binarization;
        }
        else
        {
            _preview.SetImage(null);
            _previewCaption.Text = "";
        }
    }

    /// <summary>缩略图：白底、等比缩放居中。</summary>
    private static Bitmap Thumbnail(PixelSnapshot image)
    {
        var thumbnail = new Bitmap(ThumbnailPixels, ThumbnailPixels);
        using var source = DrawingImageConverter.ToBitmap(image);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        float scale = Math.Min((ThumbnailPixels - 4f) / source.Width, (ThumbnailPixels - 4f) / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        graphics.DrawImage(source, (ThumbnailPixels - w) / 2, (ThumbnailPixels - h) / 2, w, h);
        return thumbnail;
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
        _libraryBar.Enabled = !busy;
        foreach (ToolStripItem item in _editBar.Items)
            item.Enabled = !busy;
        _reviewTools.Enabled = _viewer.Enabled = _grid.Enabled = _tiles.Enabled = !busy;
        _recognize.Enabled = _segment.Enabled = _extractAll.Enabled = !busy && _extractor != null;
        _recognize.ToolTipText = _extractor == null
            ? "自动提取服务未连接：请在配置页重载资源；仍可切换“单字框”手工补画。"
            : "OCR识别当前文字行并自动分割，识别后在右侧表格修改单字";
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

    private void Tool(ToolStrip strip, string text, Action action)
    {
        var button = new ToolStripButton(text);
        button.Click += (_, __) => TryUi(action);
        strip.Items.Add(button);
    }

    private void Icon(ToolStrip strip, ModernIconKind icon, string tip, Action action)
    {
        var button = new ToolStripButton(tip, ModernIcons.CreateBitmap(icon, ModernTheme.Dark.Text, 20))
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = tip,
            AutoToolTip = false,
        };
        button.Click += (_, __) => TryUi(action);
        strip.Items.Add(button);
    }

    private static ToolStripControlHost Host(Control control, string tip) => new ToolStripControlHost(control)
    {
        AutoSize = false,
        Size = new Size(control.Width, 30),
        Margin = new Padding(2, 1, 2, 1),
        ToolTipText = tip,
    };

    private static DataGridView Table()
    {
        return new ModernDataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
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
            foreach (var image in _thumbnails.Images.Cast<Image>().ToArray())
                image.Dispose();
            _thumbnails.Dispose();
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
            Text = "字符库 · 多图补字 / 同图多ROI / 人工分割",
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
                page.HasUnsavedCandidates
                && MessageBox.Show(
                    form,
                    "当前图还有未保存到字库的候选，关闭会丢弃它们。确定关闭？",
                    "未保存的候选",
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
