using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

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
    private readonly SplitContainer _split = new SplitContainer
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 6,
    };
    private readonly List<ActionGroup> _idleOnly = new List<ActionGroup>();
    private readonly ToolTip _tips = new ToolTip();
    private bool _splitInitialized;
    private readonly ListView _evidence = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    private readonly FlowLayoutPanel _glyphGallery = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
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
    private InspectionOptions _options = new InspectionOptions();
    private IReadOnlyList<FieldBinding> _bindings = Array.Empty<FieldBinding>();
    private TaskDataSnapshot? _taskData;
    private string? _cycleId;
    private readonly List<InspectionRegion> _regions = new List<InspectionRegion>();
    private IInspectionEngine? _engine;
    private ImageFrame? _actual;
    private ImageFrame? _reference;
    private readonly Label _referenceMode = new Label { AutoSize = true };
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
        _kind.Format += (_, e) =>
            e.Value =
                e.ListItem is EBarcodeKind type ? (type == EBarcodeKind.QrCode ? "二维码（QR）" : "一维条码")
                : e.ListItem is ERegionKind kind && kind == ERegionKind.Barcode ? "条码（自动）"
                : UiText.Get("Kind" + e.ListItem);
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
        canvas.Controls.Add(new CanvasViewBar(_viewer));
        _split.Panel1.Controls.Add(canvas);
        _split.Panel2.Controls.Add(details);
        _split.Panel2.Controls.Add(_status);
        _split.SizeChanged += (_, _) => InitializeSplit();
        // 停靠按Z序倒序处理：侧栏先占左侧，其后分隔线，余下区域给画布与结果。
        Controls.Add(_split);
        Controls.Add(
            new Label
            {
                Dock = DockStyle.Left,
                AutoSize = false,
                Width = 1,
                BackColor = SystemColors.ControlDark,
            }
        );
        Controls.Add(_sidebar);
        _status.Message = UiText.Get("Unattached");
        UpdateReferenceMode();
        WireCanvas(details, glyphTab);
    }

    private TabControl BuildResults(out TabPage glyphTab)
    {
        var details = new TabControl { Dock = DockStyle.Fill };
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
        glyphTab.Controls.Add(_glyphGallery);
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

    private void BuildSidebar(TabControl details, TabPage glyphTab)
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
        var editRois = roi.Add(new CheckBox { Text = "选中/调整ROI", AutoSize = true });
        _tips.SetToolTip(editRois, "开启后左键选中并移动/缩放ROI；按住Shift仍可新建");
        editRois.CheckedChanged += (_, _) =>
        {
            _viewer.EditRegions = editRois.Checked;
            _viewer.Invalidate();
        };
        var edit = roi.AddButton(
            "编辑ROI/规则",
            () =>
            {
                EnsureIdle();
                var edited = RegionEditor.Edit(_regions, _libraryManager, _anomalyManager);
                if (edited != null)
                {
                    SetRegions(edited);
                }
            }
        );
        _tips.SetToolTip(edit, "在表格中编辑ROI名称、类型、检查项目及规则");
        var explore = roi.AddButton(
            "采用探索文字ROI",
            () =>
            {
                EnsureIdle();
                if (LastReport == null)
                {
                    throw new InvalidOperationException("先在无参考、无ROI模式执行探索。");
                }

                var regions = LastReport
                    .Analysis.Regions.Where(r =>
                        r.RegionName.StartsWith("auto-text-", StringComparison.Ordinal)
                    )
                    .Select(r => new
                    {
                        Result = r,
                        Bounds = r.Recognition?.Bounds
                            ?? r.Findings.FirstOrDefault(f => f.Bounds.HasValue)?.Bounds,
                    })
                    .Where(r => r.Bounds.HasValue)
                    .Select(r => new InspectionRegion(
                        r.Result.RegionName,
                        ERegionKind.Text,
                        r.Bounds!.Value,
                        true
                    ))
                    .ToArray();
                if (regions.Length == 0)
                {
                    throw new InvalidOperationException("没有可采用的文字候选。");
                }

                SetRegions(regions);
            }
        );
        _tips.SetToolTip(explore, "把无参考、无ROI探索检测找到的文字区域转为ROI");
        roi.Add(_clear);
        _tips.SetToolTip(_clear, "删除全部ROI及字段绑定");
        _idleOnly.Add(roi);

        var rules = _sidebar.AddGroup("规则与数据");
        var bind = rules.AddButton(
            "字段绑定",
            () =>
            {
                EnsureIdle();
                var bindings = BindingEditor.Edit(_regions, _bindings);
                if (bindings != null)
                {
                    SetBindings(bindings);
                }
            }
        );
        _tips.SetToolTip(bind, "设置ROI之间或ROI与任务数据之间的内容约束");
        var data = rules.AddButton(
            "本次任务数据",
            () =>
            {
                EnsureIdle();
                var snapshot = BindingEditor.TaskData();
                if (snapshot != null)
                {
                    SetTaskData(snapshot.CycleId, snapshot);
                }
            }
        );
        _tips.SetToolTip(data, "为当前图像提供本周期业务数据；载入新图后自动清除");
        var thresholds = rules.AddButton(
            "阈值",
            () =>
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
            }
        );
        _tips.SetToolTip(thresholds, "墨迹、原图容差、最小面积、对比度及清晰度阈值");
        _idleOnly.Add(rules);

        var library = _sidebar.AddGroup("单字库");
        var glyphs = library.AddButton(
            "单字库",
            () =>
            {
                EnsureIdle();
                OpenLibrary(null);
            }
        );
        _tips.SetToolTip(glyphs, "管理单字模板库及版本");
        var quick = library.AddButton(
            "多图制库",
            () =>
            {
                EnsureIdle();
                GlyphQuickBuilderControl.ShowPage(
                    FindForm(),
                    _libraryManager ?? throw new InvalidOperationException("宿主未连接字库管理器。"),
                    _engine as IGlyphCandidateService,
                    _actual
                );
            }
        );
        _tips.SetToolTip(quick, "从多张图像的字符候选制作单字库新版本");
        var anomaly = library.AddButton(
            "异常模型库(B)",
            () =>
            {
                EnsureIdle();
                OpenAnomalyLibrary();
            }
        );
        _tips.SetToolTip(anomaly, "质量方法B：用良品图为ROI训练局部块异常模型，按版本发布并绑定到ROI");
        var characterAnomaly = library.AddButton(
            "字符异常模型(B)",
            () =>
            {
                EnsureIdle();
                OpenCharacterAnomalyBuilder();
            }
        );
        _tips.SetToolTip(
            characterAnomaly,
            "质量方法B逐字符模式：从多张良品图提取字符、核对身份，每个字符用多个样本训练模型，适合内容可变的文字"
        );
        _idleOnly.Add(library);

        _clear.Click += (_, _) =>
        {
            if (_active == null)
            {
                _regions.Clear();
                _bindings = Array.Empty<FieldBinding>();
                RefreshRegions();
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

    private void WireCanvas(TabControl details, TabPage glyphTab)
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
        _viewer.RegionEdited += (_, e) =>
        {
            if (_active != null || _actual == null || e.Index >= _regions.Count)
            {
                return;
            }

            int x = e.Bounds.X - (LastReport?.Analysis.OffsetX ?? 0),
                y = e.Bounds.Y - (LastReport?.Analysis.OffsetY ?? 0);
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

    /// <summary>连接宿主拥有的引擎，控件不创建或释放引擎。</summary>
    /// <param name = "engine">SDK引擎实现。</param>
    public void AttachEngine(IInspectionEngine engine)
    {
        EnsureIdle();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _status.Message = UiText.Format("Connected", engine.Capabilities);
    }

    /// <summary>设置输入图像，可选择清除旧ROI。</summary>
    /// <param name = "actual">借用实际源，内部复制为业务快照，返回后客户可释放自己的句柄；支持Gray8/Bgr24。</param>
    /// <param name = "clearRegions">是否丢弃原有坐标配置。</param>
    public void SetActualImage(DP.Vision.IImageSource actual, bool clearRegions = true)
    {
        EnsureIdle();
        // 工作台继续使用独立业务快照保存配方/报告；客户只传源，返回后可释放自己的句柄。
        _actual = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(actual);
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
    /// <param name = "reference">借用参考源，内部复制为业务快照，返回后调用方可释放；null表示清空。</param>
    /// <param name = "assumeAligned">宿主是否明确确认实际图与参考坐标已对齐。</param>
    public void SetReferenceImage(DP.Vision.IImageSource? reference, bool assumeAligned = false)
    {
        EnsureIdle();
        _reference =
            reference == null
                ? null
                : DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(reference);
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

    /// <summary>最近一次原生工作开始前捕获的输入快照。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InspectionRequest? LastRequest { get; private set; }

    /// <summary>最近一次完整报告。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InspectionReport? LastReport { get; private set; }

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
    public void AttachAnomalyLibraryManager(
        IAnomalyLibraryManager manager,
        IAnomalyModelTrainer? trainer = null
    )
    {
        EnsureIdle();
        _anomalyManager = manager ?? throw new ArgumentNullException(nameof(manager));
        _anomalyTrainer = trainer;
    }

    /// <summary>捕获当前不可变输入及配置，供持久化或无界面使用。</summary>
    public InspectionRequest CreateRequest()
    {
        EnsureIdle();
        if (_actual == null)
        {
            throw new InvalidOperationException("尚未载入待检图。");
        }

        return new InspectionRequest(
            _actual,
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
            _reference,
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
        LastRequest = null;
        LastReport = null;
        ClearGallery();
        _viewer.SetCharacters(Array.Empty<CharacterPatch>());
        _lastFindings = Array.Empty<InspectionFinding>();
        _status.ShowIdle();
        _viewer.SetOverlays(Regions, _lastFindings);
        _evidence.Items.Clear();
        foreach (var region in _regions)
        {
            _evidence.Items.Add(
                new ListViewItem(
                    new[] { "ROI", region.Name + " / " + region.Kind, region.Bounds.ToString(), "" }
                )
            );
        }
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
        var mapped = Regions
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
        _viewer.SetOverlays(mapped, _lastFindings);
        _viewer.SetCharacters(
            report.Analysis.Regions.SelectMany(r =>
                r.Segmentation?.Characters ?? Array.Empty<CharacterPatch>()
            )
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
            string details = string.Join(
                Environment.NewLine,
                report
                    .EvidenceGroups.Where(g => !g.IsBarcode)
                    .Select(g => g.RegionName + " / " + g.Summary.Code + ": " + g.Summary.Message)
            );
            if (report.EvidenceGroups.Any(g => g.IsBarcode))
            {
                reason = "点击条码汇总F项，可查看缺陷标记及完整子项明细。" + Environment.NewLine + reason;
            }

            _glyphGallery.Controls.Add(
                new Label
                {
                    Name = "GlyphEmptyReason",
                    AutoSize = true,
                    MaximumSize = new Size(1000, 0),
                    Padding = new Padding(10),
                    ForeColor = noOcr || appearanceRequested ? Color.DarkRed : Color.DimGray,
                    Text = reason + Environment.NewLine + details,
                }
            );
        }

        foreach (var region in report.Analysis.Regions)
        {
            foreach (var glyph in region.Glyphs)
            {
                var card = new FlowLayoutPanel
                {
                    Width = 330,
                    Height = 190,
                    FlowDirection = FlowDirection.LeftToRight,
                    Tag = Tuple.Create(region.RegionName, glyph),
                };
                card.Controls.Add(
                    new Label
                    {
                        Text = region.RegionName + " / " + glyph.Character.Character + " · " + glyph.Status,
                        Width = 320,
                        Height = 22,
                    }
                );
                AddPreview(card, glyph.Character.Patch, "原始单字");
                if (glyph.Comparison != null)
                {
                    AddPreview(card, glyph.Comparison.Reference, "参考");
                    AddPreview(card, glyph.Comparison.Actual, "归一实际");
                    AddPreview(card, glyph.Comparison.Delta, "差异");
                }

                AddAction(
                    card,
                    "下载单字",
                    () =>
                    {
                        using var d = new SaveFileDialog
                        {
                            Filter = "PNG|*.png",
                            FileName =
                                "glyph-"
                                + ((int)glyph.Character.Character[0]).ToString("D3")
                                + "-"
                                + glyph.Character.Character
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
                    card,
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
            var comparison = new BarcodeComparisonControl(LastRequest.Actual, barcode)
            {
                Tag = barcode,
                Width = Math.Max(600, _glyphGallery.ClientSize.Width - 30),
            };
            _glyphGallery.Controls.Add(comparison);
            _glyphGallery.Controls.SetChildIndex(comparison, 0);
            return;
        }

        info.Visible = selection != null;
        info.Text =
            selection == null
                ? ""
                : selection.Item1
                    + " / "
                    + selection.Item2.Code
                    + "：关联单字 "
                    + count
                    + " 个。可用“显示全部单字”恢复。";
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

        using var form = new Form
        {
            Text = "单字模板管理 · 任意组合复用",
            Width = 1080,
            Height = 760,
            StartPosition = FormStartPosition.CenterParent,
        };
        var editor = new GlyphLibraryControl();
        editor.AttachManager(_libraryManager);
        if (_engine is IGlyphCandidateService service)
        {
            editor.AttachCandidateService(service);
        }

        if (candidate != null)
        {
            editor.SetCandidate(candidate.Patch, candidate.Character);
        }

        form.Controls.Add(editor);
        form.ShowDialog(FindForm());
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
        var editor = new AnomalyLibraryControl();
        editor.AttachManager(_anomalyManager, _anomalyTrainer);
        editor.SetRegions(_regions);
        if (_actual != null && (LastReport?.Verdict == EInspectionVerdict.Ok))
        {
            // 最近一次检测为OK的当前图可直接作为一张良品；其余良品图在窗口中添加。
            editor.AddGoodImage(_actual, "当前图像");
        }

        form.Controls.Add(editor);
        form.ShowDialog(FindForm());
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

    private void OpenCharacterAnomalyBuilder()
    {
        if (_anomalyManager == null || _anomalyTrainer == null)
        {
            throw new InvalidOperationException("宿主尚未连接异常模型库管理器及训练实现。");
        }

        using var form = new Form
        {
            Text = "字符异常模型制作（质量方法B·逐字符）· 多图提取、每字多样本",
            Width = 1200,
            Height = 800,
            StartPosition = FormStartPosition.CenterParent,
        };
        var builder = new CharacterAnomalyBuilderControl();
        builder.AttachServices(_anomalyManager, _anomalyTrainer, _engine as IGlyphCandidateService);
        builder.SetRegions(_regions);
        if (_actual != null)
        {
            builder.AddImage(_actual, "当前图像");
        }

        form.Controls.Add(builder);
        form.ShowDialog(FindForm());
        if (
            builder.LastPublished is { } published
            && MessageBox.Show(
                this,
                $"已发布字符模型库版本 r{published.Revision}。是否把文字ROI（{string.Join("、", published.Regions)}）绑定到该版本、选择逐字符模式并启用B异常检测？",
                "绑定字符异常模型",
                MessageBoxButtons.YesNo
            ) == DialogResult.Yes
        )
        {
            SetRegions(
                _regions
                    .Select(r =>
                        published.Regions.Contains(r.Name) && r.Kind == ERegionKind.Text
                            ? r.WithAnomaly(
                                    new AnomalySettings(
                                        published.LibraryId,
                                        published.Revision,
                                        perCharacter: true
                                    )
                                )
                                .WithTasks(
                                    new RoiInspectionTasks(r.Tasks.ReadData, r.Tasks.CheckQuality, true)
                                )
                            : r
                    )
                    .ToArray()
            );
        }
    }

    private static void AddAction(Control parent, string text, Action action)
    {
        parent.Controls.Add(ActionGroup.CreateButton(text, action));
    }

    private static void AddPreview(Control card, ImageFrame frame, string label)
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
            _cancel?.Cancel();
            ClearGallery();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
