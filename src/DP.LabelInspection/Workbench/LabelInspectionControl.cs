using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>可嵌入的WinForms检测工作台；注入的引擎和图像快照由宿主拥有。</summary>
/// <remarks>
/// 布局：左侧按流程分组的操作侧栏；右侧上方为画布及视图工具条，下方为可拖动分隔的判定与证据区。
/// UI方法必须在UI线程调用；释放控件会取消自身工作，但不释放宿主引擎。
/// </remarks>
[ToolboxItem(true)]
[DefaultEvent(nameof(InspectionCompleted))]
public sealed class LabelInspectionControl : UserControl
{
    private readonly ImageViewerControl _viewer = new ImageViewerControl { Dock = DockStyle.Fill };

    /// <summary>实际检测工作台使用的渲染实现。</summary>
    public string RenderingBackend => _viewer.RenderingBackend;

    private readonly int _uiThreadId = Thread.CurrentThread.ManagedThreadId;
    private readonly ComboBox _kind = new ComboBox
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120,
        FormattingEnabled = true,
    };
    private readonly CheckBox _aligned = new CheckBox { Text = UiText.Get("Aligned"), AutoSize = true };
    private readonly Button _run = new Button
    {
        Text = UiText.Get("Run"),
        AutoSize = true,
        Padding = new Padding(0, 4, 0, 4),
    };
    private readonly Button _cancelButton = new Button
    {
        Text = UiText.Get("Cancel"),
        AutoSize = true,
        Enabled = false,
    };
    private readonly Button _clear = new Button { Text = UiText.Get("ClearRois"), AutoSize = true };
    private readonly VerdictBanner _status = new VerdictBanner();
    private readonly ActionSidebar _sidebar = new ActionSidebar { Dock = DockStyle.Left };
    private readonly ModernSplitter _split = new ModernSplitter
    {
        Dock = DockStyle.Fill,
        FixedPanel = FixedPanel.None,
        InitialPanel2Size = 0,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 6,
    };
    private readonly List<ActionGroup> _idleOnly = new List<ActionGroup>();
    private readonly ToolTip _tips = new ToolTip();
    private bool _splitInitialized;
    private readonly ModernListView _evidence = new ModernListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    // 上方为所选证据的ROI图像与结论（占满宽度），下方单字卡片按宽度自动换行；由滚动视图约束宽度并测量高度。
    private readonly FlowLayoutPanel _glyphGallery = new FlowLayoutPanel
    {
        AutoSize = false,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Padding = new Padding(4),
    };
    private readonly Label _glyphFilterInfo = new Label
    {
        Name = "GlyphFilterInfo",
        AutoSize = true,
        Visible = false,
        Margin = new Padding(8, 7, 0, 0),
    };
    private IGlyphLibraryManager? _libraryManager;
    private IAnomalyLibraryManager? _anomalyManager;
    private IAnomalyModelTrainer? _anomalyTrainer;
    private IReadOnlyDictionary<string, IAnomalyModelTrainer>? _anomalyTrainers;
    private DP.Vision.Algorithms.ITemplateLocator? _anomalyLocator;
    private InspectionOptions _options = new InspectionOptions();
    private IReadOnlyList<FieldBinding> _bindings = Array.Empty<FieldBinding>();
    private TaskDataSnapshot? _taskData;
    private string? _cycleId;
    private readonly List<InspectionRegion> _regions = new List<InspectionRegion>();
    private IInspectionEngine? _engine;
    private PixelSnapshot? _actual;
    private PixelSnapshot? _reference;
    private DP.Vision.ImageFrame? _visionActual;
    private DP.Vision.ImageFrame? _visionReference;
    private readonly Label _referenceMode = new Label { AutoSize = true };
    private bool _editRegionsMode = true;
    private WorkbenchDisplayMode _displayMode = WorkbenchDisplayMode.Result;
    private IReadOnlyList<InspectionRegion> _shownRegions = Array.Empty<InspectionRegion>();
    private IReadOnlyList<CharacterPatch> _shownCharacters = Array.Empty<CharacterPatch>();
    private readonly Label _sidebarLine = new Label
    {
        Dock = DockStyle.Left,
        AutoSize = false,
        Width = 1,
        BackColor = SystemColors.ControlDark,
    };
    private readonly CanvasViewBar _viewBar;
    private bool _sidebarVisible = true;
    private CancellationTokenSource? _cancel;
    private Task<InspectionReport>? _active;
    private int _regionNumber;
    private IReadOnlyList<InspectionFinding> _lastFindings = Array.Empty<InspectionFinding>();

    /// <summary>创建无参数、可安全用于设计器的控件。</summary>
    public LabelInspectionControl()
    {
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        MinimumSize = new Size(600, 400);
        _kind.Format += (_, e) => e.Value = FormatKind(e.ListItem!);
        foreach (var value in Enum.GetValues(typeof(ERegionKind)))
        {
            _kind.Items.Add(value);
        }

        _kind.Items.Add(EBarcodeKind.OneDimensional);
        _kind.Items.Add(EBarcodeKind.QrCode);
        _kind.SelectedItem = ERegionKind.Blank;

        var details = BuildResults(out var glyphTab);
        BuildSidebar(details, glyphTab);
        var canvas = new Panel { Dock = DockStyle.Fill };
        canvas.Controls.Add(_viewer);
        canvas.Controls.Add(_viewBar = new CanvasViewBar(_viewer));
        _split.Panel1.Controls.Add(canvas);
        _split.Panel2.Controls.Add(details);
        _split.Panel2.Controls.Add(_status);
        _split.SizeChanged += (_, _) => InitializeSplit();
        // 停靠按Z序倒序处理：侧栏先占左侧，其后分隔线，余下区域给画布与结果。
        Controls.Add(_split);
        Controls.Add(_sidebarLine);
        Controls.Add(_sidebar);
        _status.Message = UiText.Get("Unattached");
        UpdateReferenceMode();
        WireCanvas(details, glyphTab);
        ApplyDisplay();
        DrawKinds = _kind.Items.Cast<object>().Select(item => new RegionDrawKind(item, FormatKind(item))).ToArray();
        _kind.SelectedIndexChanged += (_, _) => DrawKindChanged?.Invoke(this, EventArgs.Empty);
        InspectionUiStyle.Apply(this);
    }

    private static string FormatKind(object item) =>
        item is EBarcodeKind type ? (type == EBarcodeKind.QrCode ? "二维码（QR）" : "一维条码")
        : item is ERegionKind kind && kind == ERegionKind.Barcode ? "条码（自动）"
        : UiText.Get("Kind" + item);

    private ModernTabControl BuildResults(out TabPage glyphTab)
    {
        var details = new ModernTabControl { Dock = DockStyle.Fill };
        var evidenceTab = new TabPage("检查证据");
        glyphTab = new TabPage("缺陷标记 / 单字");
        evidenceTab.Controls.Add(_evidence);
        var glyphBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(2),
        };
        AddAction(
            glyphBar,
            "显示全部单字",
            () =>
            {
                foreach (ListViewItem item in _evidence.SelectedItems.Cast<ListViewItem>().ToArray())
                {
                    item.Selected = false;
                }

                FilterGlyphs(null);
            }
        );
        glyphBar.Controls.Add(_glyphFilterInfo);
        glyphTab.Controls.Add(new ModernScrollView { Dock = DockStyle.Fill, Content = _glyphGallery });
        _glyphGallery.SizeChanged += (_, _) => FitComparisons();
        glyphTab.Controls.Add(glyphBar);
        details.TabPages.Add(evidenceTab);
        details.TabPages.Add(glyphTab);
        _evidence.Columns.Add(UiText.Get("Verdict"), 85);
        _evidence.Columns.Add(UiText.Get("Check"), 220);
        _evidence.Columns.Add(UiText.Get("Coordinates"), 150);
        _evidence.Columns.Add(UiText.Get("Description"), 550);
        _evidence.MultiSelect = false;
        _evidence.HideSelection = false;
        _evidence.Resize += (_, _) => FitDescriptionColumn();
        return details;
    }

    private void BuildSidebar(ModernTabControl details, TabPage glyphTab)
    {
        var run = _sidebar.AddGroup("检测");
        run.Add(_run);
        run.Emphasize(_run);
        run.Add(_cancelButton);
        _tips.SetToolTip(_run, "按当前ROI与规则检测已载入的待检图（F5）");
        _tips.SetToolTip(_cancelButton, "取消正在进行的检测，不生成合格结果");

        var reference = _sidebar.AddGroup("参考模式");
        reference.Add(_referenceMode);
        reference.Add(_aligned);
        _tips.SetToolTip(_aligned, "宿主已确认待检图与参考图坐标对齐时勾选，不再执行模板平移");
        var free = reference.AddButton(
            "无整图参考（可用单字库）",
            () =>
            {
                SetReferenceImage(null);
                _status.Message =
                    "已关闭整图模板模式；ROI、单字库及字段绑定保留。固定区域模板差异/模板平移不再执行。";
            }
        );
        _tips.SetToolTip(free, "关闭整图模板模式；保留ROI、单字库及字段绑定");
        _idleOnly.Add(reference);

        var roi = _sidebar.AddGroup("ROI");
        roi.Add(new Label { Text = UiText.Get("Drag"), AutoSize = true });
        roi.Add(_kind);
        var edit = roi.AddButton("编辑ROI/规则", EditRegionRules);
        _tips.SetToolTip(edit, "在表格中编辑ROI名称、类型、检查项目及规则");
        var explore = roi.AddButton("采用探索文字ROI", AdoptExploredTextRegions);
        _tips.SetToolTip(explore, "把无参考、无ROI探索检测找到的文字区域转为ROI");
        roi.Add(_clear);
        _tips.SetToolTip(_clear, "删除全部ROI及字段绑定");
        _idleOnly.Add(roi);

        var rules = _sidebar.AddGroup("规则与数据");
        var bind = rules.AddButton("字段绑定", EditFieldBindings);
        _tips.SetToolTip(bind, "设置ROI之间或ROI与任务数据之间的内容约束");
        var data = rules.AddButton("本次任务数据", EditTaskData);
        _tips.SetToolTip(data, "为当前图像提供本周期业务数据；载入新图后自动清除");
        var thresholds = rules.AddButton("阈值", EditThresholds);
        _tips.SetToolTip(thresholds, "墨迹、原图容差、最小面积、对比度及清晰度阈值");
        _idleOnly.Add(rules);

        var library = _sidebar.AddGroup("单字库");
        var glyphs = library.AddButton("单字库", OpenGlyphLibrary);
        _tips.SetToolTip(glyphs, "管理单字模板库及版本");
        var quick = library.AddButton("多图制库", OpenGlyphQuickBuilder);
        _tips.SetToolTip(quick, "从多张图像的字符候选制作单字库新版本");
        var anomaly = library.AddButton("异常模型库(B)", OpenAnomalyLibraryManager);
        _tips.SetToolTip(anomaly, "质量方法B：管理异常模型库（版本、导入导出、归档），也可按ROI快速训练");
        var batch = library.AddButton("批量训练(B)", OpenAnomalyBatchTraining);
        _tips.SetToolTip(
            batch,
            "质量方法B：多张良品图、每图框多个样本，整ROI与逐字符模型一次训练并作为一个版本发布，可保存采集下次继续"
        );
        _idleOnly.Add(library);

        _clear.Click += (_, _) =>
        {
            if (_active == null)
            {
                ClearRegions();
            }
        };
        _cancelButton.Click += (_, _) => _cancel?.Cancel();
        _run.Click += async (_, _) =>
        {
            try
            {
                await RunInspectionAsync();
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed)
                {
                    _status.Message = UiText.Get("Cancelled");
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    _status.Message = UiText.Format("Failed", error.Message);
                    if (error is ArgumentException)
                    {
                        MessageBox.Show(
                            this,
                            error.Message,
                            "检测配置未通过",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                    }
                }
            }
        };
    }

    private void WireCanvas(ModernTabControl details, TabPage glyphTab)
    {
        _evidence.SelectedIndexChanged += (_, _) =>
        {
            if (
                _evidence.SelectedItems.Count == 1
                && _evidence.SelectedItems[0].Tag is Tuple<string, InspectionFinding> selection
            )
            {
                FilterGlyphs(selection);
                details.SelectedTab = glyphTab;
            }
        };
        _viewer.FindingSelected += (_, e) =>
        {
            if (e.Index < _lastFindings.Count)
            {
                var finding = _lastFindings[e.Index];
                foreach (ListViewItem item in _evidence.Items)
                {
                    if (
                        item.Tag is Tuple<string, InspectionFinding> tag
                        && ReferenceEquals(tag.Item2, finding)
                    )
                    {
                        item.Selected = true;
                        item.EnsureVisible();
                        FilterGlyphs(tag);
                        details.SelectedTab = glyphTab;
                        break;
                    }
                }
            }
        };
        _viewer.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && _active == null && _viewer.SelectedRegionIndex >= 0)
            {
                RemoveSelectedRegion();
                e.Handled = true;
            }
        };
        _viewer.RegionEdited += (_, e) =>
        {
            if (_active != null || _actual == null || e.Index >= _regions.Count)
            {
                return;
            }

            int dx = 0, dy = 0;
            if (LastReport != null && !LastReport.Analysis.TryGetTranslation(out dx, out dy))
            {
                _status.Message = "当前报告包含非整数平移变换，不能按轴对齐ROI编辑。";
                return;
            }

            int x = e.Bounds.X - dx,
                y = e.Bounds.Y - dy;
            if (x < 0 || y < 0 || x + e.Bounds.Width > _actual.Width || y + e.Bounds.Height > _actual.Height)
            {
                _status.Message = "调整超出配方坐标范围，未保存。";
                return;
            }

            var old = _regions[e.Index];
            _regions[e.Index] = new InspectionRegion(
                old.Name,
                old.Kind,
                new PixelRect(x, y, e.Bounds.Width, e.Bounds.Height),
                old.SingleLine,
                old.Kind == ERegionKind.Text || old.Kind == ERegionKind.Barcode ? old.Field : null,
                old.Anomaly
            ).WithTasks(old.Tasks);
            RefreshRegions();
            _status.Message = "ROI已修改；旧检测结果已清除，请重新检测。";
        };
        _viewer.RegionDrawn += (_, e) =>
        {
            if (_active != null)
            {
                return;
            }

            string name;
            do
            {
                name = "ROI-" + ++_regionNumber;
            } while (_regions.Any(r => r.Name == name));
            var barcodeType = _kind.SelectedItem is EBarcodeKind selectedType
                ? selectedType
                : EBarcodeKind.Auto;
            var kind =
                _kind.SelectedItem is EBarcodeKind ? ERegionKind.Barcode : (ERegionKind)_kind.SelectedItem!;
            _regions.Add(
                new InspectionRegion(
                    name,
                    kind,
                    e.Bounds,
                    singleLine: kind == ERegionKind.Text,
                    field: kind == ERegionKind.Barcode ? new FieldSettings(barcodeType: barcodeType) : null
                )
            );
            RefreshRegions();
        };
    }

    private void InitializeSplit()
    {
        // SplitContainer在尺寸过小时设置最小尺寸会抛出异常，因此首次获得实际高度后再设定比例。
        const int canvasMin = 160,
            resultsMin = 120;
        if (_splitInitialized || _split.Height < canvasMin + resultsMin + _split.SplitterWidth + 20)
        {
            return;
        }

        _splitInitialized = true;
        _split.SplitterDistance = Math.Max(
            canvasMin,
            Math.Min(_split.Height - resultsMin - _split.SplitterWidth, (int)(_split.Height * .64))
        );
        _split.Panel1MinSize = canvasMin;
        _split.Panel2MinSize = resultsMin;
    }

    private void FitDescriptionColumn()
    {
        if (_evidence.Columns.Count < 4)
        {
            return;
        }

        int used = 0;
        for (int i = 0; i < _evidence.Columns.Count - 1; i++)
        {
            used += _evidence.Columns[i].Width;
        }

        _evidence.Columns[_evidence.Columns.Count - 1].Width = Math.Max(
            200,
            _evidence.ClientSize.Width - used - 4
        );
    }

    /// <inheritdoc/>
    protected override void OnLayout(LayoutEventArgs e)
    {
        // 左停靠只使用当前宽度；字体变化后按各组所需宽度重设，避免裁切按钮文字。
        int width = _sidebar.GetPreferredSize(Size.Empty).Width;
        if (_sidebar.Width != width)
        {
            _sidebar.Width = width;
        }

        base.OnLayout(e);
    }

    private void SetBusy(bool busy)
    {
        _run.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _viewer.Enabled = !busy;
        foreach (var group in _idleOnly)
        {
            group.Enabled = !busy;
        }

        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5 && _run.Enabled && _run.CanFocus)
        {
            _run.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>完整报告显示后在UI线程触发。</summary>
    public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

    /// <summary>
    /// 是否显示左侧操作栏（检测、参考模式、ROI、规则与数据、单字库）。宿主把这些操作放到自己的工具栏/属性页时设为false，
    /// 并改用对应的公开方法（<see cref="EditRegionRules"/>、<see cref="OpenGlyphLibrary"/>等）。
    /// </summary>
    [DefaultValue(true)]
    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set
        {
            _sidebarVisible = value;
            _sidebar.Visible = value;
            _sidebarLine.Visible = value;
        }
    }

    /// <summary>是否显示画布上方的缩放工具条；宿主提供自己的工具栏时可隐藏，并调用<see cref="FitToWindow"/>等。</summary>
    [DefaultValue(true)]
    public bool CanvasToolbarVisible
    {
        get => _canvasToolbarVisible;
        set
        {
            _canvasToolbarVisible = value;
            _viewBar.Visible = value;
        }
    }

    private bool _canvasToolbarVisible = true;

    /// <summary>画布适应窗口。</summary>
    public void FitToWindow() => _viewer.FitToWindow();

    /// <summary>画布1:1显示。</summary>
    public void ActualSize() => _viewer.ActualSize();

    /// <summary>以画布中心缩放。</summary>
    /// <param name = "factor">缩放倍数，大于1放大。</param>
    public void Zoom(float factor) => _viewer.ZoomAt(factor, new Point(_viewer.Width / 2, _viewer.Height / 2));

    /// <summary>左键拖动新建ROI时可选的区域类型（含一维条码/二维码）。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<RegionDrawKind> DrawKinds { get; }

    /// <summary>左键拖动新建ROI时使用的区域类型。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RegionDrawKind DrawKind
    {
        get => DrawKinds.First(k => Equals(k.Item, _kind.SelectedItem));
        set => _kind.SelectedItem = (value ?? throw new ArgumentNullException(nameof(value))).Item;
    }

    /// <summary><see cref="DrawKind"/>改变后触发。</summary>
    public event EventHandler? DrawKindChanged;

    /// <summary>
    /// 是否可直接编辑ROI（默认开启）：单击选中、拖动框内移动、拖动控制点缩放，在框外拖动新建，Delete删除选中ROI。
    /// 关闭后左键只新建ROI或点选检测证据。
    /// </summary>
    [DefaultValue(true)]
    public bool EditRegionsMode
    {
        get => _editRegionsMode;
        set
        {
            if (_editRegionsMode == value)
            {
                return;
            }

            _editRegionsMode = value;
            ApplyDisplay();
            EditRegionsModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary><see cref="EditRegionsMode"/>改变后触发。</summary>
    public event EventHandler? EditRegionsModeChanged;

    /// <summary>画布显示内容：输入图像、ROI或检测结果；只影响显示，不清除结果。</summary>
    [DefaultValue(WorkbenchDisplayMode.Result)]
    public WorkbenchDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode == value)
            {
                return;
            }

            _displayMode = value;
            ApplyDisplay();
            DisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary><see cref="DisplayMode"/>改变后触发。</summary>
    public event EventHandler? DisplayModeChanged;

    /// <summary>在画布上选中指定名称的ROI（显示控制点，可直接调整）；null、找不到或画布不显示ROI时取消选中。</summary>
    /// <param name = "name">ROI名称。</param>
    public void SelectRegion(string? name)
    {
        int index = name == null || _displayMode == WorkbenchDisplayMode.InputImage
            ? -1
            : _regions.FindIndex(r => r.Name == name);
        _viewer.SelectedRegionIndex = index < _shownRegions.Count ? index : -1;
    }

    /// <summary>删除画布上选中的ROI及引用它的字段绑定；未选中时返回false。</summary>
    public bool RemoveSelectedRegion()
    {
        EnsureIdle();
        int index = _viewer.SelectedRegionIndex;
        if (index < 0 || index >= _regions.Count)
        {
            return false;
        }

        string name = _regions[index].Name;
        _regions.RemoveAt(index);
        _bindings = _bindings
            .Where(b => b.Target != name && !(b.Source == EBindingSource.Region && b.Key == name))
            .ToArray();
        RefreshRegions();
        _status.Message = "已删除ROI“" + name + "”。";
        return true;
    }

    /// <summary>ROI或字段绑定改变（画布绘制/调整、编辑、清空、载入配方等）后触发。</summary>
    public event EventHandler? RegionsChanged;

    /// <summary>开始或结束试检测时触发；宿主据此启用/禁用自己的按钮。</summary>
    public event EventHandler? BusyChanged;

    /// <summary>画布上选中的ROI名称；未选中为null。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedRegionName =>
        _viewer.SelectedRegionIndex >= 0 && _viewer.SelectedRegionIndex < _regions.Count
            ? _regions[_viewer.SelectedRegionIndex].Name
            : null;

    /// <summary>画布上选中的ROI改变后触发。</summary>
    public event EventHandler? SelectedRegionChanged
    {
        add => _viewer.SelectedRegionChanged += value;
        remove => _viewer.SelectedRegionChanged -= value;
    }

    /// <summary>当前检测阈值。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InspectionOptions Options => _options;

    /// <summary>当前字段绑定。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<FieldBinding> Bindings => _bindings;

    /// <summary>已连接的字库管理器；未连接为null。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IGlyphLibraryManager? LibraryManager => _libraryManager;

    /// <summary>已连接的异常模型库管理器；未连接为null。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IAnomalyLibraryManager? AnomalyLibraryManager => _anomalyManager;

    /// <summary>请求取消正在进行的试检测，不等待。</summary>
    public void CancelInspection() => _cancel?.Cancel();

    /// <summary>在窗口中编辑ROI名称、类型、检测项目及规则，保存后生效。</summary>
    public void EditRegionRules()
    {
        EnsureIdle();
        var edited = RegionEditor.Edit(_regions, _libraryManager, _anomalyManager);
        if (edited != null)
        {
            SetRegions(edited);
        }
    }

    /// <summary>把无参考、无ROI探索检测找到的文字区域转为ROI。</summary>
    public void AdoptExploredTextRegions()
    {
        EnsureIdle();
        if (LastReport == null)
        {
            throw new InvalidOperationException("先在无参考、无ROI模式执行探索。");
        }

        var regions = LastReport
            .Analysis.Regions.Where(r => r.RegionName.StartsWith("auto-text-", StringComparison.Ordinal))
            .Select(r => new
            {
                Result = r,
                Bounds = r.Recognition?.Bounds ?? r.Findings.FirstOrDefault(f => f.Bounds.HasValue)?.Bounds,
            })
            .Where(r => r.Bounds.HasValue)
            .Select(r => new InspectionRegion(r.Result.RegionName, ERegionKind.Text, r.Bounds!.Value, true))
            .ToArray();
        if (regions.Length == 0)
        {
            throw new InvalidOperationException("没有可采用的文字候选。");
        }

        SetRegions(regions);
    }

    /// <summary>删除全部ROI及字段绑定。</summary>
    public void ClearRegions()
    {
        EnsureIdle();
        _regions.Clear();
        _bindings = Array.Empty<FieldBinding>();
        RefreshRegions();
    }

    /// <summary>在窗口中设置ROI之间或ROI与任务数据之间的内容约束。</summary>
    public void EditFieldBindings()
    {
        EnsureIdle();
        var bindings = BindingEditor.Edit(_regions, _bindings);
        if (bindings != null)
        {
            SetBindings(bindings);
        }
    }

    /// <summary>在窗口中为当前图像录入本周期业务数据；载入新图后自动清除。</summary>
    public void EditTaskData()
    {
        EnsureIdle();
        var snapshot = BindingEditor.TaskData();
        if (snapshot != null)
        {
            SetTaskData(snapshot.CycleId, snapshot);
        }
    }

    /// <summary>在窗口中编辑墨迹、原图容差、最小面积、对比度及清晰度阈值。</summary>
    public void EditThresholds()
    {
        EnsureIdle();
        string? text = EditorDialogs.Ask(
            "阈值：墨迹,原图容差,最小面积,对比度,清晰度",
            string.Join(
                ",",
                _options.InkThreshold,
                _options.TolerancePixels,
                _options.MinimumDefectArea,
                _options.MinimumContrast,
                _options.MinimumSharpness
            )
        );
        if (text == null)
        {
            return;
        }

        var p = text.Split(',');
        if (p.Length != 5)
        {
            throw new ArgumentException("需要5个参数。");
        }

        _options = new InspectionOptions(
            int.Parse(p[0]),
            int.Parse(p[1]),
            int.Parse(p[2]),
            double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture)
        );
        RefreshRegions();
    }

    /// <summary>打开单字模板库管理窗口。</summary>
    public void OpenGlyphLibrary()
    {
        EnsureIdle();
        OpenLibrary(null);
    }

    /// <summary>打开多图制库窗口，从多张图像的字符候选制作单字库新版本。</summary>
    public void OpenGlyphQuickBuilder()
    {
        EnsureIdle();
        GlyphQuickBuilderControl.ShowPage(
            FindForm(),
            _libraryManager ?? throw new InvalidOperationException("宿主未连接字库管理器。"),
            _engine as IGlyphCandidateService,
            _actual
        );
    }

    /// <summary>打开异常模型库（质量方法B）管理窗口。</summary>
    public void OpenAnomalyLibraryManager()
    {
        EnsureIdle();
        OpenAnomalyLibrary();
    }

    /// <summary>打开异常模型批量训练（质量方法B）窗口。</summary>
    public void OpenAnomalyBatchTraining()
    {
        EnsureIdle();
        OpenBatchTraining();
    }

    /// <summary>打开“字库”窗口：单字库管理与多图制库两个分页，字库相关操作都在其中完成。</summary>
    /// <param name = "startWithQuickBuilder">是否先显示多图制库分页。</param>
    public void OpenGlyphLibraries(bool startWithQuickBuilder = false)
    {
        EnsureIdle();
        var manager = _libraryManager ?? throw new InvalidOperationException("宿主尚未连接字库管理器。");
        var service = _engine as IGlyphCandidateService;
        using var form = new Form
        {
            Text = "字库 · 单字模板管理 / 多图制库",
            Width = 1380,
            Height = 960,
            MinimumSize = new Size(1120, 780),
            StartPosition = FormStartPosition.CenterParent,
        };
        var tabs = new ModernTabControl { Dock = DockStyle.Fill };
        var library = new GlyphLibraryControl { Dock = DockStyle.Fill };
        library.AttachManager(manager);
        if (service != null)
        {
            library.AttachCandidateService(service);
        }

        var builder = new GlyphQuickBuilderControl { Dock = DockStyle.Fill };
        builder.AttachServices(manager, service, library.SelectedLibraryId);
        if (_actual != null)
        {
            builder.SetImage(_actual);
        }

        var libraryPage = new TabPage("单字库");
        libraryPage.Controls.Add(library);
        var builderPage = new TabPage("多图制库");
        builderPage.Controls.Add(builder);
        tabs.TabPages.Add(libraryPage);
        tabs.TabPages.Add(builderPage);
        tabs.SelectedTab = startWithQuickBuilder ? builderPage : libraryPage;
        // 浏览和制库共用选库；切回浏览时读取制库新修订，不重建制库草稿。
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == libraryPage)
                library.RefreshLibraries(builder.SelectedLibraryId);
            else if (tabs.SelectedTab == builderPage)
            {
                try { builder.RefreshLibraries(library.SelectedLibraryId); }
                catch (InvalidOperationException error)
                {
                    MessageBox.Show(form, error.Message, "制库目标保持不变");
                }
            }
        };
        form.Controls.Add(tabs);
        GlyphQuickBuilderControl.GuardClose(form, builder);
        InspectionUiStyle.Apply(form);
        form.ShowDialog(FindForm());
    }

    /// <summary>
    /// 打开“异常模型”窗口（质量方法B）：异常模型库管理与批量训练两个分页；关闭时对新发布的版本询问是否绑定到ROI。
    /// </summary>
    /// <param name = "startWithBatchTraining">是否先显示批量训练分页。</param>
    public void OpenAnomalyLibraries(bool startWithBatchTraining = false)
    {
        EnsureIdle();
        var manager = _anomalyManager ?? throw new InvalidOperationException("宿主尚未连接异常模型库管理器。");
        using var form = new Form
        {
            Text = "异常模型（质量方法B）· 模型库 / 批量训练",
            Width = 1400,
            Height = 900,
            MinimumSize = new Size(1080, 700),
            StartPosition = FormStartPosition.CenterParent,
        };
        var tabs = new ModernTabControl { Dock = DockStyle.Fill };
        var editor = CreateAnomalyEditor();
        var libraryPage = new TabPage("异常模型库");
        libraryPage.Controls.Add(editor);
        tabs.TabPages.Add(libraryPage);
        var batchPage = new TabPage("批量训练");
        AnomalyBatchTrainingControl? batch = null;
        (string LibraryId, int Revision)? before = null;
        if (_anomalyTrainer != null)
        {
            batch = PrepareBatch();
            before = batch.LastPublished;
            batchPage.Controls.Add(batch);
        }
        else
        {
            batchPage.Controls.Add(new Label { Text = "宿主未连接训练实现，不能批量训练。", AutoSize = true, Padding = new Padding(12) });
        }

        tabs.TabPages.Add(batchPage);
        tabs.SelectedTab = startWithBatchTraining ? batchPage : libraryPage;
        // 批量训练发布的新版本切回模型库分页即可看到。
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == libraryPage)
            {
                editor.AttachManager(manager, _anomalyTrainer);
            }
        };
        form.Controls.Add(tabs);
        InspectionUiStyle.Apply(form);
        try
        {
            form.ShowDialog(FindForm());
        }
        finally
        {
            // 训练页在本控件生命周期内保留，关闭窗口不丢失已载入的图和框。
            if (batch != null)
            {
                batchPage.Controls.Remove(batch);
            }
        }

        OfferAnomalyBinding(editor);
        if (batch != null)
        {
            OfferBatchBinding(before);
        }
    }

    /// <summary>
    /// 在当前待检图上显示宿主已有的报告（例如生产运行的报告），与试检测结果的显示一致。
    /// 报告坐标须为当前ROI/待检图坐标；调用前先用<see cref="SetActualImage"/>与<see cref="ApplyRecipe"/>载入同一帧及所用配方。
    /// </summary>
    /// <param name = "report">要显示的完整报告。</param>
    public void ShowReport(InspectionReport report)
    {
        EnsureIdle();
        if (report == null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        if (_actual == null)
        {
            throw new InvalidOperationException("尚未载入待检图。");
        }

        RefreshRegions();
        // 条码比对等明细需要请求中的原图快照。
        LastRequest = CreateRequest();
        LastReport = report;
        Display(report);
    }

    /// <summary>连接宿主拥有的引擎，控件不创建或释放引擎。</summary>
    /// <param name = "engine">SDK引擎实现。</param>
    public void AttachEngine(IInspectionEngine engine)
    {
        EnsureIdle();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _status.Message = UiText.Format("Connected", engine.Capabilities);
    }

    /// <summary>设置输入图像，可选择清除旧ROI。</summary>
    /// <param name = "actual">借用实际源，内部保留Vision租约并复制显示快照；返回后客户可释放自己的句柄。</param>
    /// <param name = "clearRegions">是否丢弃原有坐标配置。</param>
    public void SetActualImage(DP.Vision.IImageSource actual, bool clearRegions = true)
    {
        EnsureIdle();
        using var frame = new DP.Vision.ImageFrame(Guid.NewGuid().ToString("N"), actual);
        var preview = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(frame.Image);
        _visionActual?.Dispose();
        _visionActual = frame.Retain();
        _actual = preview;
        _viewer.SetImage(_actual);
        _taskData = null;
        _cycleId = null;
        if (clearRegions)
        {
            _regions.Clear();
            _bindings = Array.Empty<FieldBinding>();
        }

        RefreshRegions();
        UpdateReferenceMode();
    }

    /// <summary>设置同尺寸参考，自由模式可传null。</summary>
    /// <param name = "reference">借用参考源，内部保留Vision租约并复制显示快照；null表示清空。</param>
    /// <param name = "assumeAligned">宿主是否明确确认实际图与参考坐标已对齐。</param>
    public void SetReferenceImage(DP.Vision.IImageSource? reference, bool assumeAligned = false)
    {
        EnsureIdle();
        using var frame = reference == null ? null : new DP.Vision.ImageFrame(Guid.NewGuid().ToString("N"), reference);
        var preview = frame == null ? null : DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(frame.Image);
        _visionReference?.Dispose();
        _visionReference = frame?.Retain();
        _reference = preview;
        _aligned.Checked = assumeAligned;
        UpdateReferenceMode();
    }

    private void UpdateReferenceMode()
    {
        bool mismatch =
            _reference != null
            && _actual != null
            && (_reference.Width != _actual.Width || _reference.Height != _actual.Height);
        _referenceMode.ForeColor = mismatch ? Color.Firebrick : SystemColors.ControlText;
        // 侧栏较窄，按行显示模式、参考及待检尺寸。
        _referenceMode.Text =
            _reference == null
                ? "无整图参考" + Environment.NewLine + "可独立绑定单字库"
                : "整图模板"
                    + Environment.NewLine
                    + "参考 "
                    + _reference.Width
                    + "×"
                    + _reference.Height
                    + (
                        _actual == null
                            ? ""
                            : Environment.NewLine + "待检 " + _actual.Width + "×" + _actual.Height
                    )
                    + (mismatch ? Environment.NewLine + "尺寸不符" : "");
    }

    /// <summary>替换ROI配置并复制输入集合。</summary>
    /// <param name = "regions">ROI定义集合。</param>
    public void SetRegions(IEnumerable<InspectionRegion> regions)
    {
        EnsureIdle();
        if (regions == null)
        {
            throw new ArgumentNullException(nameof(regions));
        }

        var copy = regions.ToArray();
        _regions.Clear();
        _regions.AddRange(copy);
        RefreshRegions();
    }

    /// <summary>供宿主持久化的当前区域快照。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<InspectionRegion> Regions => Array.AsReadOnly(_regions.ToArray());

    /// <summary>异步检测一次不可变配置快照；调用方必须在UI线程调用。</summary>
    /// <param name = "options">可选检测阈值。</param>
    /// <returns>完整SDK检测报告。</returns>
    public async Task<InspectionReport> RunInspectionAsync(InspectionOptions? options = null)
    {
        EnsureIdle();
        if (_engine == null || _actual == null)
        {
            throw new InvalidOperationException(UiText.Error("InputRequired"));
        }

        if (options != null)
        {
            _options = options;
        }

        var request = CreateRequest();
        LastRequest?.Dispose();
        LastRequest = request;
        LastReport = null;
        _cancel = new CancellationTokenSource();
        SetBusy(true);
        _status.ShowRunning();
        _status.Message = UiText.Get("Running");
        try
        {
            _active = _engine.InspectAsync(request, _cancel.Token);
            var report = await _active;
            if (!IsDisposed && !Disposing)
            {
                LastReport = report;
                Display(report);
                InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(report));
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                _status.ShowCancelled();
                _status.Message = UiText.Get("Cancelled");
            }

            throw;
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _status.ShowFailed();
                _status.Message = UiText.Format("Failed", error.Message);
            }

            throw;
        }
        finally
        {
            _active = null;
            _cancel.Dispose();
            _cancel = null;
            if (!IsDisposed && !Disposing)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>宿主释放其拥有的引擎前，取消并等待原生工作；原生调用采用协作式取消。</summary>
    /// <returns>活动任务完成后的等待结果。</returns>
    public async Task CancelAndWaitAsync()
    {
        _cancel?.Cancel();
        var active = _active;
        if (active != null)
        {
            try
            {
                await active;
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>最近一次请求；借用至下次配置更改、检测或控件释放，需长期使用时自行在有效期内复制像素。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InspectionRequest? LastRequest { get; private set; }

    /// <summary>最近一次完整报告。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InspectionReport? LastReport { get; private set; }

    /// <summary>当前是否有尚未结束的原生试检测；宿主据此禁用资源替换与提交。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsInspectionRunning => _active != null;

    /// <summary>连接独立字库编辑操作，引擎存储仍由宿主配置。</summary>
    /// <param name = "manager">宿主拥有的字库管理器。</param>
    public void AttachLibraryManager(IGlyphLibraryManager manager)
    {
        EnsureIdle();
        _libraryManager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <summary>连接异常模型库（质量方法B）的管理及训练，引擎使用的模型库仍由宿主配置。</summary>
    /// <param name = "manager">宿主拥有的异常模型库管理器。</param>
    /// <param name = "trainer">宿主拥有的训练实现；null时只能管理、导入导出。</param>
    /// <param name="trainers">可选厂商训练器；只影响制作，不改变生产资产绑定。</param>
    /// <param name = "locator">批量训练中内容固定样本框自动对齐所用的模板定位实现（宿主拥有，例如DP.Vision OpenCvTemplateLocator）；null时不自动对齐。</param>
    public void AttachAnomalyLibraryManager(
        IAnomalyLibraryManager manager,
        IAnomalyModelTrainer? trainer = null,
        DP.Vision.Algorithms.ITemplateLocator? locator = null,
        IReadOnlyDictionary<string, IAnomalyModelTrainer>? trainers = null
    )
    {
        EnsureIdle();
        _anomalyTrainers = trainers;
        if (_batch != null && trainer != null) _batch.AttachServices(manager, trainer, _engine as IGlyphCandidateService, locator, trainers);
        _anomalyManager = manager ?? throw new ArgumentNullException(nameof(manager));
        _anomalyTrainer = trainer;
        _anomalyLocator = locator;
    }

    /// <summary>捕获当前Vision帧租约及不可变配置；调用方负责释放返回的请求。</summary>
    public InspectionRequest CreateRequest()
    {
        EnsureIdle();
        if (_actual == null)
        {
            throw new InvalidOperationException("尚未载入待检图。");
        }

        return InspectionRequest.FromVision(
            _visionActual!,
            new InspectionRecipe(
                "WinForms configuration",
                _actual.Width,
                _actual.Height,
                _reference == null ? EInspectionMode.Free : EInspectionMode.Template,
                _aligned.Checked ? EAlignmentMode.AssumeAligned : EAlignmentMode.Translation,
                _regions,
                _options,
                _bindings
            ),
            _visionReference,
            _cycleId,
            _taskData
        );
    }

    /// <summary>加载固定配方，不静默升级所绑定的参考版本。</summary>
    /// <param name = "recipe">待加载的固定配方，保留已绑定的参考版本。</param>
    public void ApplyRecipe(InspectionRecipe recipe)
    {
        EnsureIdle();
        if (_actual == null || recipe.Width != _actual.Width || recipe.Height != _actual.Height)
        {
            throw new ArgumentException("配方与待检图尺寸不一致。");
        }

        if (recipe.Mode == EInspectionMode.Free)
        {
            _visionReference?.Dispose();
            _visionReference = null;
            _reference = null;
        }
        else if (_reference == null)
        {
            throw new ArgumentException("模板配方需先载入参考图。");
        }

        _options = recipe.Options;
        _aligned.Checked = recipe.Alignment == EAlignmentMode.AssumeAligned;
        SetRegions(recipe.Regions);
        _bindings = recipe.Bindings;
        _taskData = null;
        _cycleId = null;
        UpdateReferenceMode();
    }

    /// <summary>设置命名内容约束，不修改固定Expected值。</summary>
    /// <param name = "bindings">命名ROI之间或ROI到任务数据的约束集合。</param>
    public void SetBindings(IEnumerable<FieldBinding> bindings)
    {
        EnsureIdle();
        if (bindings == null)
        {
            throw new ArgumentNullException(nameof(bindings));
        }

        var validated = new InspectionRecipe(
            "bindings",
            _actual?.Width ?? 1,
            _actual?.Height ?? 1,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            _regions,
            _options,
            bindings
        );
        _bindings = validated.Bindings;
        RefreshRegions();
    }

    /// <summary>仅为当前图像提供宿主关联数据；后续SetActualImage会清除它，避免复用上一周期数据。</summary>
    /// <param name = "captureCycleId">当前图像的宿主采集周期标识。</param>
    /// <param name = "data">本周期不可变业务数据，null清除关联数据。</param>
    public void SetTaskData(string? captureCycleId, TaskDataSnapshot? data)
    {
        EnsureIdle();
        _cycleId = captureCycleId;
        _taskData = data;
        RefreshRegions();
        _status.Message =
            data == null
                ? "本次任务数据已清空"
                : "已绑定本次任务数据：" + captureCycleId + " / " + data.Source;
    }

    private void EnsureIdle()
    {
        if (IsDisposed || Disposing)
        {
            throw new ObjectDisposedException(nameof(LabelInspectionControl));
        }

        if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException(UiText.Error("UiThreadRequired"));
        }

        if (_active != null)
        {
            throw new InvalidOperationException(UiText.Error("InspectionBusy"));
        }
    }

    private void RefreshRegions()
    {
        LastRequest?.Dispose();
        LastRequest = null;
        LastReport = null;
        ClearGallery();
        _lastFindings = Array.Empty<InspectionFinding>();
        _status.ShowIdle();
        Show(Regions, Array.Empty<CharacterPatch>());
        _evidence.Items.Clear();
        foreach (var region in _regions)
        {
            _evidence.Items.Add(
                new ListViewItem(
                    new[] { "ROI", region.Name + " / " + region.Kind, region.Bounds.ToString(), "" }
                )
            );
        }

        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Show(IReadOnlyList<InspectionRegion> regions, IReadOnlyList<CharacterPatch> characters)
    {
        _shownRegions = regions;
        _shownCharacters = characters;
        ApplyDisplay();
    }

    // 按显示模式过滤叠加内容；ROI序号与_regions一致，选中/编辑/删除仍按序号对应。
    private void ApplyDisplay()
    {
        bool regions = _displayMode != WorkbenchDisplayMode.InputImage;
        bool result = _displayMode == WorkbenchDisplayMode.Result;
        _viewer.AllowRegionDrawing = regions;
        _viewer.EditRegions = regions && _editRegionsMode;
        _viewer.DrawOutsideRegions = true;
        _viewer.SetCharacters(result ? _shownCharacters : Array.Empty<CharacterPatch>());
        _viewer.SetOverlays(
            regions ? _shownRegions : Array.Empty<InspectionRegion>(),
            result ? _lastFindings : Array.Empty<InspectionFinding>()
        );
    }

    private void Display(InspectionReport report)
    {
        _status.ShowVerdict(report.Verdict);
        _status.Message = double.IsNaN(report.Analysis.Contrast)
            ? $"{report.Verdict.ToString().ToUpperInvariant()} · {report.ElapsedMilliseconds:F1} ms · 逐ROI数据/印刷质量；未执行整图可检性评估"
            : UiText.Format(
                "Summary",
                report.Verdict.ToString().ToUpperInvariant(),
                report.ElapsedMilliseconds,
                report.Analysis.Contrast,
                report.Analysis.Sharpness
            );
        _evidence.Items.Clear();
        var findings = new List<InspectionFinding>();
        foreach (var group in report.EvidenceGroups)
        {
            AddEvidence(
                string.IsNullOrEmpty(group.RegionName) ? UiText.Get("Global") : group.RegionName,
                group.Summary,
                findings.Count + 1
            );
            findings.Add(group.Summary);
        }

        _lastFindings = findings.AsReadOnly();
        bool translationOnly = report.Analysis.TryGetTranslation(out _, out _);
        if (!translationOnly)
            _status.Message = "当前报告包含仿射变换，轴对齐ROI叠加已隐藏；请使用矩阵几何预览。";
        var mapped = (translationOnly ? Regions : Array.Empty<InspectionRegion>())
            .Select(r =>
                new InspectionRegion(
                    r.Name,
                    r.Kind,
                    new PixelRect(
                        r.Bounds.X + report.Analysis.OffsetX,
                        r.Bounds.Y + report.Analysis.OffsetY,
                        r.Bounds.Width,
                        r.Bounds.Height
                    ),
                    r.SingleLine,
                    r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode ? r.Field : null,
                    r.Anomaly
                ).WithTasks(r.Tasks)
            )
            .ToArray();
        Show(
            mapped,
            report.Analysis.Regions.SelectMany(r => r.Segmentation?.Characters ?? Array.Empty<CharacterPatch>()).ToArray()
        );
        ClearGallery();
        if (!report.Analysis.Regions.Any(r => r.Glyphs.Count > 0))
        {
            bool noOcr = report.Analysis.Regions.Any(r => r.Findings.Any(f => f.Code == "ocr_unavailable"));
            bool appearanceRequested = _regions.Any(r =>
                r.Kind == ERegionKind.Text && (r.Field.LibraryId != null || r.Field.EqualCells)
            );
            string reason =
                noOcr ? "尚未加载OCR模型，未执行文字识别。请点击顶部“加载OCR模型”，选择识别模型后重新检测。"
                : !appearanceRequested
                    ? "本次未启用单字外观检查，仅显示OCR/内容规则/绑定校验结果。如需检查缺墨、断笔等，请为文字ROI绑定字形库及版本。"
                : "没有可展示的单字结果。已请求外观检查，请查看分割状态、字库版本或缺字原因。";
            // 各ROI的明细已在“检查证据”中，这里只给出没有单字结果的原因。
            var empty = new Label
            {
                Name = "GlyphEmptyReason",
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                Padding = new Padding(6),
                ForeColor = noOcr || appearanceRequested ? ModernTheme.Dark.Warning : ModernTheme.Dark.TextSecondary,
                Text = reason,
            };
            _glyphGallery.Controls.Add(empty);
            _glyphGallery.SetFlowBreak(empty, true);
        }

        foreach (var region in report.Analysis.Regions)
        {
            foreach (var glyph in region.Glyphs)
            {
                var card = new GlyphResultCard(
                    region.RegionName,
                    glyph,
                    _regions.FirstOrDefault(r => r.Name == region.RegionName)?.Field?.MaximumDifference
                );
                AddAction(
                    card.Actions,
                    "下载单字",
                    () =>
                    {
                        using var d = new SaveFileDialog
                        {
                            Filter = "PNG|*.png",
                            FileName =
                                "glyph-U"
                                + char.ConvertToUtf32(glyph.Character.Character, 0).ToString("X4")
                                + ".png",
                        };
                        if (d.ShowDialog() == DialogResult.OK)
                        {
                            using var image = DrawingImageConverter.ToBitmap(glyph.Character.Patch);
                            image.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                );
                AddAction(
                    card.Actions,
                    "确认补库",
                    () =>
                    {
                        EnsureIdle();
                        OpenLibrary(glyph.Character);
                    }
                );
                _glyphGallery.Controls.Add(card);
            }
        }
    }

    private void FilterGlyphs(Tuple<string, InspectionFinding>? selection)
    {
        var barcode =
            selection == null
                ? null
                : LastReport?.EvidenceGroups.FirstOrDefault(g =>
                    g.Children.Count > 0 && ReferenceEquals(g.Summary, selection.Item2)
                );
        var existing = _glyphGallery
            .Controls.Cast<Control>()
            .Where(c => c.Name == "BarcodeComparison" || c.Name == "RoiComparison")
            .ToArray();
        if (barcode != null && existing.Any(c => ReferenceEquals(c.Tag, barcode)))
        {
            return;
        }

        foreach (var old in existing)
        {
            _glyphGallery.Controls.Remove(old);
            old.Dispose();
        }

        var empty = _glyphGallery.Controls.Find("GlyphEmptyReason", false).FirstOrDefault();
        if (empty != null)
        {
            empty.Visible = barcode == null;
        }

        var cards = _glyphGallery
            .Controls.Cast<Control>()
            .Where(c => c.Tag is Tuple<string, GlyphInspection>)
            .ToArray();
        bool single =
            selection != null
            && selection.Item2.Bounds.HasValue
            && cards.Any(c =>
            {
                var tag = (Tuple<string, GlyphInspection>)c.Tag!;
                return tag.Item1 == selection.Item1
                    && tag.Item2.Character.Bounds.Equals(selection.Item2.Bounds.Value);
            });
        int count = 0;
        foreach (var card in cards)
        {
            var tag = (Tuple<string, GlyphInspection>)card.Tag!;
            bool visible =
                selection == null
                || (
                    tag.Item1 == selection.Item1
                    && (!single || tag.Item2.Character.Bounds.Equals(selection.Item2.Bounds!.Value))
                );
            card.Visible = visible;
            if (visible)
            {
                count++;
            }
        }

        var info = _glyphFilterInfo;
        if (barcode != null && LastRequest != null)
        {
            if (barcode.IsBarcode)
            {
                foreach (var card in cards)
                {
                    card.Visible = false;
                }
            }

            info.Visible = false;
            var comparison = new BarcodeComparisonControl(LastRequest.CreateActualSnapshot(), barcode)
            {
                Tag = barcode,
            };
            _glyphGallery.Controls.Add(comparison);
            _glyphGallery.Controls.SetChildIndex(comparison, 0);
            _glyphGallery.SetFlowBreak(comparison, true);
            FitComparisons();
            info.Visible = !barcode.IsBarcode && count > 0;
            info.Text = barcode.RegionName + "：" + count + " 个相关单字";
            return;
        }

        info.Visible = selection != null;
        info.Text = selection == null ? "" : selection.Item1 + "：" + count + " 个相关单字";
    }

    // 证据的ROI图像与结论占满结果区宽度，高度随宽度在合理范围内变化。
    private void FitComparisons()
    {
        int width = Math.Max(320, _glyphGallery.ClientSize.Width - _glyphGallery.Padding.Horizontal - 8);
        foreach (var comparison in _glyphGallery.Controls.OfType<BarcodeComparisonControl>())
        {
            comparison.Size = new Size(width, Math.Max(LogicalToDeviceUnits(320), Math.Min(LogicalToDeviceUnits(520), width / 2)));
        }
    }

    private void AddEvidence(string region, InspectionFinding finding, int index)
    {
        _evidence.Items.Add(
            new ListViewItem(
                new[]
                {
                    finding.Verdict.ToString().ToUpperInvariant(),
                    "F" + index + " · " + region + " / " + finding.Code,
                    finding.Bounds?.ToString() ?? "",
                    finding.Message,
                }
            )
            {
                Tag = Tuple.Create(region, finding),
                ForeColor =
                    finding.Verdict == EInspectionVerdict.Ng ? Color.FromArgb(198, 40, 40)
                    : finding.Verdict == EInspectionVerdict.Review ? Color.FromArgb(230, 100, 0)
                    : SystemColors.WindowText,
            }
        );
    }

    private void OpenLibrary(CharacterPatch? candidate)
    {
        if (_libraryManager == null)
        {
            throw new InvalidOperationException("宿主尚未连接字库管理器。");
        }

        if (candidate != null)
        {
            GlyphQuickBuilderControl.ShowPage(FindForm(), _libraryManager,
                _engine as IGlyphCandidateService, candidate.Patch, candidateCharacter: candidate.Character);
        }
        else
        {
            OpenGlyphLibraries();
        }
    }

    private void OpenAnomalyLibrary()
    {
        if (_anomalyManager == null)
        {
            throw new InvalidOperationException("宿主尚未连接异常模型库管理器。");
        }

        using var form = new Form
        {
            Text = "异常模型库（质量方法B）· 良品训练、按版本绑定",
            Width = 1080,
            Height = 700,
            StartPosition = FormStartPosition.CenterParent,
        };
        var editor = CreateAnomalyEditor();
        form.Controls.Add(editor);
        form.ShowDialog(FindForm());
        OfferAnomalyBinding(editor);
    }

    private AnomalyLibraryControl CreateAnomalyEditor()
    {
        var editor = new AnomalyLibraryControl { Dock = DockStyle.Fill };
        editor.AttachManager(_anomalyManager!, _anomalyTrainer);
        editor.SetRegions(_regions);
        if (_actual != null && (LastReport?.Verdict == EInspectionVerdict.Ok))
        {
            // 最近一次检测为OK的当前图可直接作为一张良品；其余良品图在窗口中添加。
            editor.AddGoodImage(_actual, "当前图像");
        }

        return editor;
    }

    private void OfferAnomalyBinding(AnomalyLibraryControl editor)
    {
        if (editor.LastPublished is not { } published)
        {
            return;
        }

        // 窗口中改过尺寸/位置的ROI须一并写回配方，否则位置相关模型的裁图尺寸与配方不符。
        var changed = editor
            .ChangedBounds.Where(p => published.Regions.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        string boxes =
            changed.Count == 0
                ? ""
                : "并把改过的ROI框写回配方（"
                    + string.Join(
                        "、",
                        changed.Select(p =>
                            $"{p.Key}→{p.Value.X},{p.Value.Y} {p.Value.Width}×{p.Value.Height}"
                        )
                    )
                    + "）";
        if (
            MessageBox.Show(
                this,
                $"已发布模型库版本 r{published.Revision}。是否把训练的ROI（{string.Join("、", published.Regions)}）绑定到该版本、启用B异常检测{boxes}？",
                "绑定异常模型",
                MessageBoxButtons.YesNo
            ) == DialogResult.Yes
        )
        {
            SetRegions(
                _regions
                    .Select(r =>
                        published.Regions.Contains(r.Name)
                            ? (changed.TryGetValue(r.Name, out var box) ? r.WithBounds(box) : r)
                                .WithAnomaly(new AnomalySettings(published.LibraryId, published.Revision))
                                .WithTasks(
                                    new RoiInspectionTasks(r.Tasks.ReadData, r.Tasks.CheckQuality, true)
                                )
                            : r
                    )
                    .ToArray()
            );
        }
    }

    private AnomalyBatchTrainingControl? _batch;
    private Action<AnomalyTrainingSession, string>? _saveTraining;
    private Func<
        string,
        IReadOnlyList<InspectionRegion>,
        (AnomalyTrainingSession, IReadOnlyDictionary<AnomalyTrainingSample, int[]>)
    >? _loadTraining;

    /// <summary>连接批量训练采集的保存与打开（宿主用存储实现，参数为会话/文件路径及当前配方ROI）。</summary>
    /// <param name = "save">保存采集。</param>
    /// <param name = "load">打开采集，返回会话及各逐字符样本保存时取消的字符序号。</param>
    public void AttachAnomalyTrainingProjects(
        Action<AnomalyTrainingSession, string> save,
        Func<
            string,
            IReadOnlyList<InspectionRegion>,
            (AnomalyTrainingSession, IReadOnlyDictionary<AnomalyTrainingSample, int[]>)
        > load
    )
    {
        _saveTraining = save ?? throw new ArgumentNullException(nameof(save));
        _loadTraining = load ?? throw new ArgumentNullException(nameof(load));
    }

    private void OpenBatchTraining()
    {
        var batch = PrepareBatch();
        var before = batch.LastPublished;
        using (
            var form = new Form
            {
                Text = "异常模型批量训练（质量方法B）· 多图多框、一次训练",
                Width = 1400,
                Height = 900,
                StartPosition = FormStartPosition.CenterParent,
            }
        )
        {
            form.Controls.Add(batch);
            try
            {
                form.ShowDialog(FindForm());
            }
            finally
            {
                form.Controls.Remove(batch);
            }
        }

        OfferBatchBinding(before);
    }

    /// <summary>准备在本控件生命周期内保留的批量训练页，并同步当前配方ROI。</summary>
    private AnomalyBatchTrainingControl PrepareBatch()
    {
        if (_anomalyManager == null || _anomalyTrainer == null)
        {
            throw new InvalidOperationException("宿主尚未连接异常模型库管理器及训练实现。");
        }

        // 训练页在本控件生命周期内保留，关闭窗口不丢失已载入的图和框。
        if (_batch == null)
        {
            _batch = new AnomalyBatchTrainingControl();
            _batch.AttachServices(
                _anomalyManager,
                _anomalyTrainer,
                _engine as IGlyphCandidateService,
                _anomalyLocator, _anomalyTrainers
            );
            if (_actual != null)
            {
                _batch.AddImage(_actual, "当前图像");
            }
        }

        if (_saveTraining != null && _loadTraining != null)
        {
            var load = _loadTraining;
            _batch.SaveProjectHandler = _saveTraining;
            _batch.LoadProjectHandler = path => load(path, _regions.ToArray());
        }

        _batch.SetRecipe(_regions);
        _batch.Dock = DockStyle.Fill;
        return _batch;
    }

    private void OfferBatchBinding((string LibraryId, int Revision)? before)
    {
        if (_batch?.LastPublished is not { } published || Equals(published, before))
        {
            return;
        }

        var bound = _batch.Session.Bindings(published.LibraryId, published.Revision, _batch.PublishedSupportsInkLoss);
        if (bound.Count == 0)
        {
            return;
        }

        var resized = bound
            .Where(b => !_regions.Single(r => r.Name == b.Name).Bounds.Equals(b.Bounds))
            .Select(b => $"{b.Name}→{b.Bounds.Width}×{b.Bounds.Height}")
            .ToArray();
        if (
            MessageBox.Show(
                this,
                $"已发布模型库版本 r{published.Revision}。是否把以下配方ROI绑定到该版本并启用B异常检测：{string.Join("、", bound.Select(b => b.Name))}？"
                    + (
                        resized.Length == 0
                            ? ""
                            : $"\r\n内容固定模型的尺寸与配方不同，将按中心改为模型尺寸：{string.Join("、", resized)}"
                    ),
                "绑定异常模型",
                MessageBoxButtons.YesNo
            ) == DialogResult.Yes
        )
        {
            var byName = bound.ToDictionary(b => b.Name, StringComparer.Ordinal);
            SetRegions(_regions.Select(r => byName.TryGetValue(r.Name, out var b) ? b : r).ToArray());
        }
    }

    private static void AddAction(Control parent, string text, Action action)
    {
        var button = InspectionUiStyle.CreateButton(text);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private static void AddPreview(Control card, PixelSnapshot frame, string label)
    {
        var box = new PictureBox
        {
            Width = 76,
            Height = 100,
            Image = DrawingImageConverter.ToBitmap(frame),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        box.AccessibleName = label;
        card.Controls.Add(box);
    }

    private void ClearGallery()
    {
        foreach (Control card in _glyphGallery.Controls.Cast<Control>().ToArray())
        {
            foreach (var picture in card.Controls.OfType<PictureBox>())
            {
                picture.Image?.Dispose();
            }

            card.Dispose();
        }

        _glyphGallery.Controls.Clear();
        _glyphFilterInfo.Visible = false;
        _glyphFilterInfo.Text = "";
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_active != null)
                throw new InvalidOperationException("Await CancelAndWaitAsync before disposal.");
            LastRequest?.Dispose();
            LastRequest = null;
            _visionActual?.Dispose();
            _visionActual = null;
            _visionReference?.Dispose();
            _visionReference = null;
            ClearGallery();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
