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
/// <remarks>UI方法必须在UI线程调用；释放控件会取消自身工作，但不释放宿主引擎。</remarks>
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
    private readonly Button _run = new Button { Text = UiText.Get("Run"), AutoSize = true };
    private readonly Button _cancelButton = new Button { Text = UiText.Get("Cancel"), AutoSize = true };
    private readonly Button _clear = new Button { Text = UiText.Get("ClearRois"), AutoSize = true };
    private readonly Label _status = new Label
    {
        AutoSize = true,
        Text = UiText.Get("Unattached"),
        Padding = new Padding(6),
    };
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
    private IGlyphLibraryManager? _libraryManager;
    private InspectionOptions _options = new InspectionOptions();
    private IReadOnlyList<FieldBinding> _bindings = Array.Empty<FieldBinding>();
    private TaskDataSnapshot? _taskData;
    private string? _cycleId;
    private readonly List<InspectionRegion> _regions = new List<InspectionRegion>();
    private IInspectionEngine? _engine;
    private ImageFrame? _actual;
    private ImageFrame? _reference;
    private readonly Label _referenceMode = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
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
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(6),
        };
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
        toolbar.Controls.AddRange(
            new Control[]
            {
                new Label
                {
                    Text = UiText.Get("Drag"),
                    AutoSize = true,
                    Padding = new Padding(0, 6, 0, 0),
                },
                _kind,
                _aligned,
                _run,
                _cancelButton,
                _clear,
            }
        );
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        var details = new TabControl { Dock = DockStyle.Fill };
        var evidenceTab = new TabPage("检查证据");
        var glyphTab = new TabPage("缺陷标记 / 单字");
        evidenceTab.Controls.Add(_evidence);
        glyphTab.Controls.Add(_glyphGallery);
        details.TabPages.Add(evidenceTab);
        details.TabPages.Add(glyphTab);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_viewer, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(details, 0, 3);
        var editRois = new CheckBox { Text = "选中/调整ROI", AutoSize = true };
        editRois.CheckedChanged += (_, _) =>
        {
            _viewer.EditRegions = editRois.Checked;
            _viewer.Invalidate();
        };
        toolbar.Controls.Add(editRois);
        AddAction(
            toolbar,
            "放大+",
            () => _viewer.ZoomAt(1.25f, new Point(_viewer.Width / 2, _viewer.Height / 2))
        );
        AddAction(
            toolbar,
            "缩小−",
            () => _viewer.ZoomAt(.8f, new Point(_viewer.Width / 2, _viewer.Height / 2))
        );
        toolbar.Controls.Add(
            new Label
            {
                Text = "画布：DP.Vision",
                AutoSize = true,
                Padding = new Padding(4, 6, 4, 0),
            }
        );
        AddAction(toolbar, "1:1", () => _viewer.ActualSize());
        AddAction(toolbar, "适应窗口", () => _viewer.FitToWindow());
        var zoomLabel = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        void UpdateZoom()
        {
            zoomLabel.Text = $"{_viewer.ImageScale * 100:0.#}% · 滚轮缩放 / 中或右键拖动 / Home复位";
        }

        _viewer.ViewChanged += (_, _) => UpdateZoom();
        UpdateZoom();
        toolbar.Controls.Add(zoomLabel);
        AddAction(
            toolbar,
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
        _evidence.MultiSelect = false;
        _evidence.HideSelection = false;
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
                _status.Text = "调整超出配方坐标范围，未保存。";
                return;
            }

            var old = _regions[e.Index];
            _regions[e.Index] = new InspectionRegion(
                old.Name,
                old.Kind,
                new PixelRect(x, y, e.Bounds.Width, e.Bounds.Height),
                old.SingleLine,
                old.Kind == ERegionKind.Text || old.Kind == ERegionKind.Barcode ? old.Field : null
            ).WithTasks(old.Tasks);
            RefreshRegions();
            _status.Text = "ROI已修改；旧检测结果已清除，请重新检测。";
        };
        AddAction(
            toolbar,
            "编辑ROI/规则",
            () =>
            {
                EnsureIdle();
                var edited = RegionEditor.Edit(_regions, _libraryManager);
                if (edited != null)
                {
                    SetRegions(edited);
                }
            }
        );
        AddAction(
            toolbar,
            "无整图参考（可用单字库）",
            () =>
            {
                SetReferenceImage(null);
                _status.Text =
                    "已关闭整图模板模式；ROI、单字库及字段绑定保留。固定区域模板差异/模板平移不再执行。";
            }
        );
        toolbar.Controls.Add(_referenceMode);
        UpdateReferenceMode();
        AddAction(
            toolbar,
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
        AddAction(
            toolbar,
            "本次任务数据",
            () =>
            {
                EnsureIdle();
                var data = BindingEditor.TaskData();
                if (data != null)
                {
                    SetTaskData(data.CycleId, data);
                }
            }
        );
        AddAction(
            toolbar,
            "单字库",
            () =>
            {
                EnsureIdle();
                OpenLibrary(null);
            }
        );
        AddAction(
            toolbar,
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
        AddAction(
            toolbar,
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
        AddAction(
            toolbar,
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
        _evidence.Columns.Add(UiText.Get("Verdict"), 85);
        _evidence.Columns.Add(UiText.Get("Check"), 220);
        _evidence.Columns.Add(UiText.Get("Coordinates"), 150);
        _evidence.Columns.Add(UiText.Get("Description"), 550);
        Controls.Add(layout);
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
                    _status.Text = UiText.Get("Cancelled");
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    _status.Text = UiText.Format("Failed", error.Message);
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

    /// <summary>完整报告显示后在UI线程触发。</summary>
    public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

    /// <summary>连接宿主拥有的引擎，控件不创建或释放引擎。</summary>
    /// <param name = "engine">SDK引擎实现。</param>
    public void AttachEngine(IInspectionEngine engine)
    {
        EnsureIdle();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _status.Text = UiText.Format("Connected", engine.Capabilities);
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
        _referenceMode.Text =
            _reference == null
                ? "模式：无整图参考 · 可独立绑定单字库"
                : "模式：整图模板 · 参考 "
                    + _reference.Width
                    + "×"
                    + _reference.Height
                    + (_actual == null ? "" : " / 待检 " + _actual.Width + "×" + _actual.Height)
                    + (mismatch ? "（尺寸不符）" : "");
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
        _run.Enabled = _clear.Enabled = _kind.Enabled = _aligned.Enabled = _viewer.Enabled = false;
        _status.Text = UiText.Get("Running");
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
                _status.Text = UiText.Get("Cancelled");
            }

            throw;
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _status.Text = UiText.Format("Failed", error.Message);
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
                _run.Enabled = _clear.Enabled = _kind.Enabled = _aligned.Enabled = _viewer.Enabled = true;
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
        _status.Text =
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
        _status.Text = double.IsNaN(report.Analysis.Contrast)
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
                    r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode ? r.Field : null
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

        var info = _glyphGallery.Controls.Find("GlyphFilterInfo", false).FirstOrDefault();
        if (info == null)
        {
            info = new Label
            {
                Name = "GlyphFilterInfo",
                AutoSize = true,
                Padding = new Padding(6),
            };
            _glyphGallery.Controls.Add(info);
            _glyphGallery.Controls.SetChildIndex(info, 0);
        }

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

    private static void AddAction(Control parent, string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "操作未完成");
            }
        };
        parent.Controls.Add(b);
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
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel?.Cancel();
            ClearGallery();
        }

        base.Dispose(disposing);
    }
}
