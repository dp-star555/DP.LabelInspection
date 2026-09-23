using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Wpf;

/// <summary>原生WPF图像、ROI及结果控件，不使用WinForms桥接，也不拥有后台或引擎。</summary>
public sealed class LabelInspectionControl : UserControl, IDisposable
{
    private readonly Canvas _canvas = new Canvas { Background = Brushes.WhiteSmoke, ClipToBounds = true };
    private readonly DP.Vision.WPF.VisionCanvasControl _vision = new DP.Vision.WPF.VisionCanvasControl();
    private DP.Vision.IImageSource? _source;
    private ImageFrame? _displayedActual;
    private string _frameId = "";
    private long _sequence;
    private bool _disposed;

    /// <summary>主图和只读证据使用的原生分块渲染器，不使用WindowsFormsHost。</summary>
    public string RenderingBackend => "DP.Vision.WPF / native tiled DrawingContext";

    /// <summary>计入预算的图块/掩码像素，不含源快照和图形子系统开销。</summary>
    public long CachedDisplayPixelBytes => _vision.CachedPixelBytes;

    /// <summary>主渲染器保留的原图布局。</summary>
    public string DisplayPixelLayout => _source?.Info.Layout.ToString() ?? "";

    private readonly ListBox _evidence = new ListBox();
    private readonly WrapPanel _glyphs = new WrapPanel();
    private readonly ComboBox _kind = new ComboBox
    {
        Width = 130,
        ItemsSource = Enum.GetValues(typeof(ERegionKind)),
        SelectedItem = ERegionKind.Text,
    };
    private readonly TextBlock _status = new TextBlock
    {
        Text = "宿主注入引擎；OCR成功不代表外观合格。",
        Margin = new Thickness(6),
    };
    private readonly CheckBox _aligned = new CheckBox
    {
        Content = "上游已对齐",
        IsChecked = true,
        Margin = new Thickness(8),
    };
    private readonly List<InspectionRegion> _regions = new List<InspectionRegion>();
    private IInspectionEngine? _engine;
    private ImageFrame? _actual,
        _reference;
    private CancellationTokenSource? _cancel;
    private Task<InspectionReport>? _active;
    private IReadOnlyList<FieldBinding> _bindings = Array.Empty<FieldBinding>();
    private TaskDataSnapshot? _taskData;
    private string? _cycleId;
    private readonly TextBlock _referenceMode = new TextBlock
    {
        Margin = new Thickness(6),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private InspectionReport? _report;
    private Point? _start;
    private Rectangle? _drag;
    private int _next;
    private readonly ComboBox _roiTasks = new ComboBox { Width = 170, Margin = new Thickness(4) };
    private readonly CheckBox _readData = new CheckBox
    {
        Content = "读取实际数据",
        Margin = new Thickness(6),
    };
    private readonly CheckBox _checkQuality = new CheckBox
    {
        Content = "检查印刷质量",
        Margin = new Thickness(6),
    };
    private readonly TextBox _guide = new TextBox
    {
        Width = 180,
        Margin = new Thickness(4),
        ToolTip = "文字/码的引导值；等格且不读数据时仅作为格位标签",
    };

    /// <summary>构造无参数且与厂商无关的控件。</summary>
    public LabelInspectionControl()
    {
        var layout = new DockPanel();
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        toolbar.Children.Add(_kind);
        toolbar.Children.Add(_aligned);
        toolbar.Children.Add(
            new TextBlock
            {
                Text = "画布：DP.Vision.WPF",
                Margin = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var run = new Button { Content = "开始检测", Margin = new Thickness(4) };
        run.Click += async (_, _) =>
        {
            try
            {
                await RunInspectionAsync();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "已取消";
            }
            catch (Exception e)
            {
                _status.Text = e.Message;
            }
        };
        toolbar.Children.Add(run);
        var cancel = new Button { Content = "取消", Margin = new Thickness(4) };
        cancel.Click += (_, _) => _cancel?.Cancel();
        toolbar.Children.Add(cancel);
        var clear = new Button { Content = "清空ROI", Margin = new Thickness(4) };
        clear.Click += (_, _) =>
        {
            if (_active == null)
            {
                _regions.Clear();
                _bindings = Array.Empty<FieldBinding>();
                _report = null;
                Render();
            }
        };
        toolbar.Children.Add(clear);
        var free = new Button { Content = "无整图参考（可用单字库）", Margin = new Thickness(4) };
        free.Click += (_, _) =>
        {
            try
            {
                SetReferenceImage(null);
                _status.Text = "已关闭整图模板模式，ROI及单字库绑定保留；固定模板差异/模板平移不再执行。";
            }
            catch (Exception error)
            {
                _status.Text = error.Message;
            }
        };
        toolbar.Children.Add(free);
        toolbar.Children.Add(_referenceMode);
        UpdateReferenceMode();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        var tasksBar = new WrapPanel { Margin = new Thickness(6) };
        tasksBar.Children.Add(
            new TextBlock { Text = "单ROI项目", VerticalAlignment = VerticalAlignment.Center }
        );
        tasksBar.Children.Add(_roiTasks);
        tasksBar.Children.Add(_readData);
        tasksBar.Children.Add(_checkQuality);
        tasksBar.Children.Add(
            new TextBlock { Text = "引导值/格位标签", VerticalAlignment = VerticalAlignment.Center }
        );
        tasksBar.Children.Add(_guide);
        _roiTasks.SelectionChanged += (_, _) =>
        {
            var selected = _regions.FirstOrDefault(r => r.Name == _roiTasks.SelectedItem as string);
            if (selected == null)
            {
                return;
            }

            _readData.IsChecked = selected.Tasks.ReadData;
            _checkQuality.IsChecked = selected.Tasks.CheckQuality;
            _guide.Text = selected.Field.Expected ?? "";
        };
        var applyTasks = new Button { Content = "应用到选中ROI", Margin = new Thickness(4) };
        applyTasks.Click += (_, _) =>
        {
            try
            {
                if (_roiTasks.SelectedItem is string name)
                {
                    ConfigureRoiTasks(
                        name,
                        _readData.IsChecked == true,
                        _checkQuality.IsChecked == true,
                        string.IsNullOrEmpty(_guide.Text) ? null : _guide.Text
                    );
                }
            }
            catch (Exception e)
            {
                _status.Text = e.Message;
            }
        };
        tasksBar.Children.Add(applyTasks);
        DockPanel.SetDock(tasksBar, Dock.Top);
        layout.Children.Add(tasksBar);
        DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(_status);
        var tabs = new TabControl { Height = 230 };
        tabs.Items.Add(new TabItem { Header = "证据", Content = _evidence });
        tabs.Items.Add(
            new TabItem
            {
                Header = "单字 / 参考 / 归一实际 / 差异",
                Content = new ScrollViewer
                {
                    Content = _glyphs,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            }
        );
        DockPanel.SetDock(tabs, Dock.Bottom);
        layout.Children.Add(tabs);
        layout.Children.Add(_canvas);
        Content = layout;
        _canvas.Children.Add(_vision);
        _canvas.SizeChanged += (_, _) =>
        {
            _vision.Width = _canvas.ActualWidth;
            _vision.Height = _canvas.ActualHeight;
        };
        _canvas.MouseLeftButtonDown += Begin;
        _canvas.MouseMove += Move;
        _canvas.MouseLeftButtonUp += End;
        Unloaded += (_, _) =>
        {
            CancelDrag();
            _cancel?.Cancel();
        };
        _canvas.PreviewMouseWheel += (_, _) => CancelDrag();
        _canvas.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Home)
            {
                CancelDrag();
            }
        };
        _canvas.LostMouseCapture += (_, _) =>
        {
            if (_start.HasValue && !_canvas.IsMouseCaptured)
            {
                CancelDrag();
            }
        };
    }

    /// <summary>在UI线程发出的报告通知。</summary>
    public event EventHandler<WpfInspectionCompletedEventArgs>? InspectionCompleted;

    /// <summary>注入宿主拥有的引擎。</summary>
    /// <param name = "engine">宿主拥有的检测引擎，控件不负责释放。</param>
    public void AttachEngine(IInspectionEngine engine)
    {
        Idle();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _status.Text = engine.Capabilities.ToString();
    }

    /// <summary>替换不可变实际图像。</summary>
    /// <param name = "image">借用实际源，内部复制为业务快照，返回后客户可释放自己的句柄；支持Gray8/Bgr24。</param>
    /// <param name = "clearRegions">是否清除之前的ROI坐标配置，默认true。</param>
    public void SetActualImage(DP.Vision.IImageSource image, bool clearRegions = true)
    {
        Idle();
        // 工作台保存独立业务快照；客户源只在调用期间借用，不暴露内部像素租约。
        _actual = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(image);
        _taskData = null;
        _cycleId = null;
        if (clearRegions)
        {
            _regions.Clear();
            _bindings = Array.Empty<FieldBinding>();
        }

        _report = null;
        Render();
        _vision.FitToWindow();
        UpdateReferenceMode();
    }

    /// <summary>设置可选参考像素及对齐策略。</summary>
    /// <param name = "image">借用同尺寸参考源，内部复制为业务快照，返回后客户可释放；null表示清空。</param>
    /// <param name = "assumeAligned">是否由宿主明确声明坐标已对齐，不代表自动配准。</param>
    public void SetReferenceImage(DP.Vision.IImageSource? image, bool assumeAligned = false)
    {
        Idle();
        _reference =
            image == null ? null : DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter.ToLabel(image);
        _aligned.IsChecked = assumeAligned;
        UpdateReferenceMode();
    }

    private void UpdateReferenceMode()
    {
        bool mismatch =
            _reference != null
            && _actual != null
            && (_reference.Width != _actual.Width || _reference.Height != _actual.Height);
        _referenceMode.Foreground = mismatch ? Brushes.Firebrick : Brushes.Black;
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

    /// <summary>更新一个ROI的数据/质量项目及可选引导，保留字库版本与算法参数。</summary>
    /// <param name = "name">要更新的已有ROI名称。</param>
    /// <param name = "readData">是否启用实际数据读取项目。</param>
    /// <param name = "checkQuality">是否启用印刷质量项目。</param>
    /// <param name = "expected">可选独立预期引导值，不用它修正实际读数。</param>
    public void ConfigureRoiTasks(string name, bool readData, bool checkQuality, string? expected)
    {
        Idle();
        int index = _regions.FindIndex(r => r.Name == name);
        if (index < 0)
        {
            throw new ArgumentException("ROI不存在。", nameof(name));
        }

        var r = _regions[index];
        var f = r.Field;
        FieldSettings? field =
            r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode
                ? new FieldSettings(
                    f.LibraryId,
                    f.LibraryRevision,
                    expected,
                    f.Pattern,
                    f.AllowedCharacters,
                    f.MinimumLength,
                    f.MaximumLength,
                    f.EqualCells,
                    f.MaximumDifference,
                    f.GlyphTolerance,
                    f.MinimumConfidence,
                    f.BarcodePrint,
                    f.BarcodeType
                )
                : null;
        _regions[index] = new InspectionRegion(r.Name, r.Kind, r.Bounds, r.SingleLine, field).WithTasks(
            new RoiInspectionTasks(readData, checkQuality)
        );
        _report = null;
        Render();
        _status.Text = "已更新ROI项目；缺能力在检测时直接判本ROI NG。";
    }

    /// <summary>替换ROI和规则绑定，不升级字库版本。</summary>
    /// <param name = "regions">新的不可变ROI配置集合，保留固定字库版本。</param>
    public void SetRegions(IEnumerable<InspectionRegion> regions)
    {
        Idle();
        var copy = regions.ToArray();
        _regions.Clear();
        _regions.AddRange(copy);
        _report = null;
        Render();
    }

    /// <summary>为宿主驱动的WPF流程设置可移植内容约束。</summary>
    /// <param name = "bindings">命名ROI到其他ROI或本轮任务字段的内容约束。</param>
    public void SetBindings(IEnumerable<FieldBinding> bindings)
    {
        Idle();
        _bindings = new InspectionRecipe(
            "bindings",
            _actual?.Width ?? 1,
            _actual?.Height ?? 1,
            EInspectionMode.Free,
            EAlignmentMode.AssumeAligned,
            _regions,
            bindings: bindings
        ).Bindings;
        _report = null;
        Render();
    }

    /// <summary>仅提供当前采集的数据，切换实际图像会清除这些数据。</summary>
    /// <param name = "captureCycleId">当前实际图像关联的采集周期标识。</param>
    /// <param name = "data">本周期不可变任务数据，null清除关联数据。</param>
    public void SetTaskData(string? captureCycleId, TaskDataSnapshot? data)
    {
        Idle();
        _cycleId = captureCycleId;
        _taskData = data;
        _report = null;
        Render();
    }

    /// <summary>不可变的当前ROI快照。</summary>
    public IReadOnlyList<InspectionRegion> Regions => Array.AsReadOnly(_regions.ToArray());

    /// <summary>通过宿主引擎执行CPU检测，再在Dispatcher线程渲染。</summary>
    /// <param name = "options">可选检测阈值，null使用默认设置；不改变实际图像。</param>
    public async Task<InspectionReport> RunInspectionAsync(InspectionOptions? options = null)
    {
        Idle();
        if (_engine == null || _actual == null)
        {
            throw new InvalidOperationException("需要图像和检测引擎。");
        }

        var request = new InspectionRequest(
            _actual,
            new InspectionRecipe(
                "WPF",
                _actual.Width,
                _actual.Height,
                _reference == null ? EInspectionMode.Free : EInspectionMode.Template,
                _aligned.IsChecked == true ? EAlignmentMode.AssumeAligned : EAlignmentMode.Translation,
                _regions,
                options,
                _bindings
            ),
            _reference,
            _cycleId,
            _taskData
        );
        _cancel = new CancellationTokenSource();
        _status.Text = "检测中…";
        _kind.IsEnabled = false;
        _aligned.IsEnabled = false;
        try
        {
            _active = _engine.InspectAsync(request, _cancel.Token);
            var result = await _active;
            _report = result;
            _status.Text =
                result.Verdict
                + " · "
                + result.ElapsedMilliseconds.ToString("F1")
                + "ms · 仅配置范围，不是整标签放行认证";
            _evidence.Items.Clear();
            foreach (var group in result.EvidenceGroups)
            {
                var children = new StackPanel();
                foreach (var child in group.Children)
                {
                    children.Children.Add(
                        new TextBlock
                        {
                            Text =
                                child.Id
                                + " / "
                                + child.Status
                                + " / "
                                + child.Finding.Code
                                + " / "
                                + child.Finding.Bounds
                                + " / "
                                + child.Finding.Message,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(16, 3, 3, 3),
                        }
                    );
                }

                _evidence.Items.Add(
                    new Expander
                    {
                        Header =
                            group.Id
                            + " · "
                            + group.RegionName
                            + " · "
                            + group.Status
                            + " · "
                            + group.Summary.Message,
                        Content = children,
                    }
                );
            }

            _glyphs.Children.Clear();
            foreach (var g in result.Analysis.Regions.SelectMany(r => r.Glyphs))
            {
                var card = new StackPanel { Margin = new Thickness(6) };
                card.Children.Add(new TextBlock { Text = g.Character.Character + " / " + g.Status });
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Preview(g.Character.Patch));
                if (g.Comparison != null)
                {
                    row.Children.Add(Preview(g.Comparison.Reference));
                    row.Children.Add(Preview(g.Comparison.Actual));
                    row.Children.Add(Preview(g.Comparison.Delta));
                }

                card.Children.Add(row);
                _glyphs.Children.Add(card);
            }

            Render();
            InspectionCompleted?.Invoke(this, new WpfInspectionCompletedEventArgs(request, result));
            return result;
        }
        finally
        {
            _active = null;
            _cancel.Dispose();
            _cancel = null;
            _kind.IsEnabled = true;
            _aligned.IsEnabled = true;
        }
    }

    /// <summary>宿主释放资源前先取消并异步等待。</summary>
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

    private void Idle()
    {
        Dispatcher.VerifyAccess();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LabelInspectionControl));
        }

        if (_active != null)
        {
            throw new InvalidOperationException("检测期间不能修改配置。");
        }
    }

    private Tuple<double, double, double> View()
    {
        return Tuple.Create(_vision.Viewport.Scale, _vision.Viewport.Origin.X, _vision.Viewport.Origin.Y);
    }

    private void Render()
    {
        string? selected = _roiTasks.SelectedItem as string;
        _roiTasks.Items.Clear();
        foreach (var item in _regions)
        {
            _roiTasks.Items.Add(item.Name);
        }

        if (selected != null && _roiTasks.Items.Contains(selected))
        {
            _roiTasks.SelectedItem = selected;
        }
        else if (_roiTasks.Items.Count > 0)
        {
            _roiTasks.SelectedIndex = 0;
        }

        CancelDrag();
        if (_actual == null || _disposed)
        {
            return;
        }

        if (!ReferenceEquals(_actual, _displayedActual))
        {
            var next = DP.LabelInspection.Adapter.Vision.VisionAdapter.CopyImage(_actual);
            _source?.Dispose();
            _source = next;
            _displayedActual = _actual;
            _frameId = Guid.NewGuid().ToString("N");
        }

        var regions = _regions.Select(r =>
            new InspectionRegion(
                r.Name,
                r.Kind,
                new PixelRect(
                    r.Bounds.X + (_report?.Analysis.OffsetX ?? 0),
                    r.Bounds.Y + (_report?.Analysis.OffsetY ?? 0),
                    r.Bounds.Width,
                    r.Bounds.Height
                ),
                r.SingleLine,
                r.Kind == ERegionKind.Text || r.Kind == ERegionKind.Barcode ? r.Field : null
            ).WithTasks(r.Tasks)
        );
        var findings =
            _report?.EvidenceGroups.Select(g => g.Summary) ?? Enumerable.Empty<InspectionFinding>();
        var characters =
            _report?.Analysis.Regions.SelectMany(r =>
                r.Segmentation?.Characters ?? Array.Empty<CharacterPatch>()
            )
            ?? Enumerable.Empty<CharacterPatch>();
        var layers = DP.LabelInspection.Adapter.Vision.VisionAdapter.LabelLayers(
            regions,
            findings,
            characters
        );
        using var frame = new DP.Vision.CanvasFrame(
            _frameId,
            ++_sequence,
            _source!,
            new DP.Vision.GeometryOverlay(_frameId, layers)
        );
        _vision.Present(frame);
    }

    private void CancelDrag()
    {
        _start = null;
        if (_drag != null)
        {
            _canvas.Children.Remove(_drag);
        }

        _drag = null;
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

    /// <summary>在Dispatcher线程释放拥有的显示租约；存在活动检测时，释放前应等待CancelAndWaitAsync。</summary>
    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        if (_active != null)
        {
            throw new InvalidOperationException("Await CancelAndWaitAsync before disposal.");
        }

        _disposed = true;
        CancelDrag();
        _vision.Dispose();
        _source?.Dispose();
        _source = null;
        _displayedActual = null;
        _actual = null;
    }

    private void Begin(object sender, MouseButtonEventArgs e)
    {
        if (_active != null || _actual == null)
        {
            return;
        }

        _start = e.GetPosition(_canvas);
        _drag = new Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_drag);
        _canvas.CaptureMouse();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_start.HasValue || _drag == null)
        {
            return;
        }

        var p = e.GetPosition(_canvas);
        Canvas.SetLeft(_drag, Math.Min(p.X, _start.Value.X));
        Canvas.SetTop(_drag, Math.Min(p.Y, _start.Value.Y));
        _drag.Width = Math.Abs(p.X - _start.Value.X);
        _drag.Height = Math.Abs(p.Y - _start.Value.Y);
    }

    private void End(object sender, MouseButtonEventArgs e)
    {
        if (!_start.HasValue || _actual == null)
        {
            return;
        }

        var p = e.GetPosition(_canvas);
        var start = _start.Value;
        _start = null;
        _canvas.ReleaseMouseCapture();
        var v = View();
        int left = Math.Max(0, (int)Math.Floor((Math.Min(p.X, start.X) - v.Item2) / v.Item1)),
            top = Math.Max(0, (int)Math.Floor((Math.Min(p.Y, start.Y) - v.Item3) / v.Item1));
        int right = Math.Min(_actual.Width, (int)Math.Ceiling((Math.Max(p.X, start.X) - v.Item2) / v.Item1)),
            bottom = Math.Min(
                _actual.Height,
                (int)Math.Ceiling((Math.Max(p.Y, start.Y) - v.Item3) / v.Item1)
            );
        if (right - left >= 4 && bottom - top >= 4)
        {
            string name;
            do
            {
                name = "ROI-" + ++_next;
            } while (_regions.Any(r => r.Name == name));
            var kind = (ERegionKind)_kind.SelectedItem;
            _regions.Add(
                new InspectionRegion(
                    name,
                    kind,
                    new PixelRect(left, top, right - left, bottom - top),
                    kind == ERegionKind.Text
                )
            );
        }

        _report = null;
        Render();
    }

    private static Image Preview(ImageFrame frame)
    {
        return new Image
        {
            Source = Source(frame),
            Width = 85,
            Height = 100,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(2),
        };
    }

    private static BitmapSource Source(ImageFrame frame)
    {
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            frame.Format == EImagePixelFormat.Gray8 ? PixelFormats.Gray8 : PixelFormats.Bgr24,
            null,
            frame.CopyPixels(),
            frame.Stride
        );
        source.Freeze();
        return source;
    }
}
