using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>与算法无关、保持比例适配的图像及ROI视图；坐标始终指向原始像素。</summary>
[ToolboxItem(true)]
public sealed class ImageViewerControl : Control
{
    // 为现有坐标和手势代码保留内部历史字段名；这里只存尺寸，不是Bitmap。
    private DP.Vision.ImageInfo? _bitmap;
    private CanvasGeometry? _geometry;
    private DP.Vision.Winform.VisionCanvasControl _renderer = new DP.Vision.Winform.VisionCanvasControl();
    private DP.Vision.IImageSource? _source;
    private string _frameId = "";
    private long _sequence;

    /// <summary>当前渲染实现；保留既有ROI事件及整数配方坐标约定。</summary>
    [Browsable(false)]
    public string RenderingBackend => "DP.Vision.Winform / tiled Gray8-native GDI+";

    /// <summary>计入缓存的原生图像/掩码像素载荷，不含源快照、路径及合成器副本。</summary>
    [Browsable(false)]
    public long CachedDisplayPixelBytes => _renderer.CachedPixelBytes;

    /// <summary>渲染器保留的原图布局，未加载图像时为空。</summary>
    [Browsable(false)]
    public string DisplayPixelLayout => _bitmap?.Layout.ToString() ?? "";

    /// <summary>显示复制后的中立区域/轮廓快照；null清除几何，不使用厂商运行时。</summary>
    /// <param name = "geometry">不可变几何快照，null清除几何显示。</param>
    public void SetGeometry(CanvasGeometry? geometry)
    {
        _geometry = geometry;
        PresentScene();
        Invalidate();
    }

    private void PresentScene()
    {
        if (_source == null)
        {
            return;
        }

        var layers = (
            _geometry == null
                ? Array.Empty<DP.Vision.CanvasLayer>()
                : DP.LabelInspection.Adapter.Vision.VisionAdapter.GeometryLayers(_geometry)
        ).Concat(
            DP.LabelInspection.Adapter.Vision.VisionAdapter.LabelLayers(
                _regions,
                _findings,
                _characters,
                false
            )
        );
        using var frame = new DP.Vision.CanvasFrame(
            _frameId,
            ++_sequence,
            _source,
            new DP.Vision.GeometryOverlay(_frameId, layers)
        );
        _renderer.Present(frame);
    }

    /// <summary>是否显示叠加标题；比较视图可仅显示缺陷框，避免编号拥挤。</summary>
    [DefaultValue(true)]
    public bool ShowFindingLabels { get; set; } = true;

    private float _zoom = 1;
    private PointF _pan;
    private Point? _panStart;
    private Point? _pressAt;
    private PointF _panOrigin;

    /// <summary>缩放或平移变化时触发，供宿主显示当前像素比例。</summary>
    public event EventHandler? ViewChanged;

    /// <summary>每个原图像素对应的屏幕像素数，1表示100%。</summary>
    [Browsable(false)]
    public float ImageScale => _bitmap == null ? 1 : Viewport().Width / _bitmap.Width;

    /// <summary>恢复居中等比例适配，不改变图像、证据或配方坐标。</summary>
    public void FitToWindow()
    {
        CancelGesture();
        _zoom = 1;
        _pan = PointF.Empty;
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>围绕客户区坐标点缩放，指针下的原图像素保持锚定。</summary>
    /// <param name = "factor">本次缩放倍率，必须有效且大于0。</param>
    /// <param name = "anchor">保持原图位置不变的客户区锚点，单位为屏幕像素。</param>
    public void ZoomAt(float factor, Point anchor)
    {
        if (float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        if (_bitmap == null || _drawing || _panStart.HasValue)
        {
            return;
        }

        var before = Viewport();
        float next = Math.Max(.125f, Math.Min(128, _zoom * factor));
        _zoom = next;
        float ratio = Viewport().Width / before.Width;
        float width = before.Width * ratio,
            height = before.Height * ratio;
        _pan = new PointF(
            anchor.X - (anchor.X - before.X) * ratio - (ClientSize.Width - width) / 2,
            anchor.Y - (anchor.Y - before.Y) * ratio - (ClientSize.Height - height) / 2
        );
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>以当前视口中心为中心，按100%显示原始像素。</summary>
    public void ActualSize()
    {
        if (_bitmap != null)
        {
            ZoomAt(1 / ImageScale, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        }
    }

    /// <summary>居中并放大原图证据矩形，不修改该矩形。</summary>
    /// <param name = "bounds">需要聚焦的原图证据范围，不修改证据。</param>
    public void FocusRegion(PixelRect bounds)
    {
        if (
            _bitmap == null
            || bounds.X < 0
            || bounds.Y < 0
            || bounds.X + bounds.Width > _bitmap.Width
            || bounds.Y + bounds.Height > _bitmap.Height
        )
        {
            return;
        }

        CancelGesture();
        float fit = FitScale();
        float scale = Math.Min(
            Math.Max(1, ClientSize.Width - 40) / (float)bounds.Width,
            Math.Max(1, ClientSize.Height - 40) / (float)bounds.Height
        );
        _zoom = Math.Max(.125f, Math.Min(128, scale / fit));
        scale = ImageScale;
        _pan = new PointF(
            (_bitmap.Width / 2f - bounds.X - bounds.Width / 2f) * scale,
            (_bitmap.Height / 2f - bounds.Y - bounds.Height / 2f) * scale
        );
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>与另一同尺寸图像视图同步原图中心及缩放比例。</summary>
    /// <param name = "source">具有相同图像尺寸的源视图，只读取其中心和比例。</param>
    public void MatchViewFrom(ImageViewerControl source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (
            ReferenceEquals(source, this)
            || _bitmap == null
            || source._bitmap == null
            || (_bitmap.Width != source._bitmap.Width || _bitmap.Height != source._bitmap.Height)
        )
        {
            return;
        }

        var view = source.Viewport();
        float scale = source.ImageScale;
        float centerX = (source.ClientSize.Width / 2f - view.X) / scale,
            centerY = (source.ClientSize.Height / 2f - view.Y) / scale;
        CancelGesture();
        _zoom = scale / FitScale();
        _pan = new PointF((_bitmap.Width / 2f - centerX) * scale, (_bitmap.Height / 2f - centerY) * scale);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGesture()
    {
        _panStart = null;
        _drawing = false;
        _editor.Cancel();
        Capture = false;
        Cursor = Cursors.Default;
    }

    // ROI的绘制、选择、移动与八控制点缩放由DP.Vision的RoiEditor按整像素规则处理；本控件只转发指针并把结果转为宿主事件。
    private readonly DP.Vision.UI.RoiEditor _editor = new DP.Vision.UI.RoiEditor();
    private bool _drawing;
    private int _selected = -1;

    /// <summary>按当前配方区域和图像尺寸重建编辑文档（区域序号即ROI标识），保留当前选择。</summary>
    private void LoadEditor()
    {
        _editor.PixelRules =
            _bitmap == null ? null : new DP.Vision.UI.RoiPixelRules(_bitmap.Width, _bitmap.Height);
        _editor.Load(
            new DP.Vision.UI.RoiDocument(
                _regions.Select(
                    (r, i) =>
                        new DP.Vision.UI.RoiDefinition(
                            i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            r.Bounds.ToGeometry(),
                            DP.Vision.UI.ERoiConstraint.AxisAligned
                        )
                )
            )
        );
        if (_selected >= 0 && _selected < _regions.Count)
        {
            _editor.Select(_selected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static PixelRect ToRect(DP.Vision.Geometry shape)
    {
        var b = shape.Bounds;
        return new PixelRect(
            (int)Math.Round(b.X),
            (int)Math.Round(b.Y),
            (int)Math.Round(b.Width),
            (int)Math.Round(b.Height)
        );
    }

    private int IndexOf(string? id)
    {
        return id != null && int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index < _regions.Count
            ? index
            : -1;
    }

    /// <summary>启用选择、移动及八控制点缩放，替代绘制；按住Shift强制新建ROI。</summary>
    [DefaultValue(false)]
    public bool EditRegions { get; set; }

    /// <summary>为宿主点编辑工具关闭左键ROI手势，保留缩放及中键平移。</summary>
    [DefaultValue(true)]
    public bool AllowRegionDrawing { get; set; } = true;

    /// <summary>
    /// 编辑模式下在所有框之外拖动时直接画新框（不必按Shift），默认false（主工作台保持按Shift新建）。
    /// 适合只编辑一个框的页面，例如异常模型制作页重画ROI。
    /// </summary>
    [DefaultValue(false)]
    public bool DrawOutsideRegions { get; set; }

    /// <summary>编辑模式下当前选中的叠加区域序号（显示八个控制点，可直接拖动调整），-1表示未选中。</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedRegionIndex
    {
        get => _selected;
        set
        {
            if (value < -1 || value >= _regions.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_selected != value)
            {
                _selected = value;
                if (!_drawing)
                {
                    _editor.Select(value < 0 ? null : value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                SelectedRegionChanged?.Invoke(this, EventArgs.Empty);
            }

            Invalidate();
        }
    }

    /// <summary>编辑模式下选中的叠加区域改变（点击选中、点空白取消或由宿主设置）。</summary>
    public event EventHandler? SelectedRegionChanged;

    /// <summary>将显示图像内的客户区点转换为原图整数像素边缘坐标。</summary>
    /// <param name = "point">客户区坐标，单位为屏幕像素。</param>
    public Point? ClientToImage(Point point)
    {
        return _bitmap != null && Viewport().Contains(point) ? ImagePoint(point) : (Point?)null;
    }

    /// <summary>将原图边缘转换为客户区坐标，不裁剪，供宿主显示编辑预览。</summary>
    /// <param name = "point">原图像素边缘坐标，可为小数。</param>
    public PointF ImageToClient(PointF point)
    {
        if (_bitmap == null)
        {
            throw new InvalidOperationException("No image.");
        }

        var view = Viewport();
        return new PointF(
            view.X + point.X * view.Width / _bitmap.Width,
            view.Y + point.Y * view.Height / _bitmap.Height
        );
    }

    /// <summary>在图像和叠加层之后绘制宿主临时编辑辅助线，不修改源像素。</summary>
    public event PaintEventHandler? ForegroundPaint;

    /// <summary>配方ROI移动或缩放后触发，由宿主提交修改。</summary>
    public event EventHandler<RegionEditedEventArgs>? RegionEdited;

    /// <summary>点击检测叠加时触发，索引从发现列表的0开始。</summary>
    public event EventHandler<FindingSelectedEventArgs>? FindingSelected;
    private IReadOnlyList<InspectionRegion> _regions = Array.Empty<InspectionRegion>();
    private IReadOnlyList<CharacterPatch> _characters = Array.Empty<CharacterPatch>();
    private IReadOnlyList<InspectionFinding> _findings = Array.Empty<InspectionFinding>();

    /// <summary>创建可安全用于设计器的视图，不加载视觉运行时。</summary>
    public ImageViewerControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(237, 241, 247);
        ResizeRedraw = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>绘制非空原图区域后触发，区域类型由宿主决定。</summary>
    public event EventHandler<RegionDrawnEventArgs>? RegionDrawn;

    /// <summary>加载快照；视图仅拥有其内部显示资源。</summary>
    /// <param name = "image">源图，null表示清空。</param>
    public void SetImage(PixelSnapshot? image)
    {
        DP.Vision.IImageSource? next = null;
        if (image != null)
        {
            next = DP.LabelInspection.Adapter.Vision.VisionAdapter.CopyImage(image);
        }

        string identity = Guid.NewGuid().ToString("N");
        try
        {
            if (next != null)
            {
                var layers = DP.LabelInspection.Adapter.Vision.VisionAdapter.LabelLayers(
                    _regions,
                    _findings,
                    _characters,
                    false
                );
                using var frame = new DP.Vision.CanvasFrame(
                    identity,
                    ++_sequence,
                    next,
                    new DP.Vision.GeometryOverlay(identity, layers)
                );
                _renderer.Present(frame);
            }
            else
            {
                _renderer.ClearImage();
            }
        }
        catch
        {
            next?.Dispose();
            throw;
        }

        var old = _source;
        _source = next;
        _bitmap = next?.Info;
        _frameId = identity;
        _geometry = null;
        old?.Dispose();
        _selected = -1;
        CancelGesture();
        LoadEditor();
        FitToWindow();
    }

    /// <summary>设置不可变叠加层。</summary>
    /// <param name = "regions">配方区域叠加。</param>
    /// <param name = "findings">检测证据。</param>
    public void SetOverlays(
        IReadOnlyList<InspectionRegion> regions,
        IReadOnlyList<InspectionFinding> findings
    )
    {
        _regions = regions ?? throw new ArgumentNullException(nameof(regions));
        _findings = findings ?? throw new ArgumentNullException(nameof(findings));
        if (_selected >= _regions.Count)
        {
            _selected = -1;
        }

        if (!_drawing)
        {
            LoadEditor();
        }

        PresentScene();
        Invalidate();
    }

    /// <summary>显示实测字符框；独立字符图块仍保存在报告中，此处不重新裁图。</summary>
    /// <param name = "characters">物理字符证据集合，只用于显示其原图范围。</param>
    public void SetCharacters(IEnumerable<CharacterPatch> characters)
    {
        _characters = Array.AsReadOnly(characters.ToArray());
        PresentScene();
        Invalidate();
    }

    private float FitScale()
    {
        return _bitmap == null
            ? 1
            : Math.Min(
                Math.Max(1, ClientSize.Width - 24) / (float)_bitmap.Width,
                Math.Max(1, ClientSize.Height - 24) / (float)_bitmap.Height
            );
    }

    private RectangleF Viewport()
    {
        if (_bitmap == null)
        {
            return RectangleF.Empty;
        }

        float scale = FitScale();
        scale = Math.Max(1 / 1024f, Math.Min(128, scale * _zoom));
        return new RectangleF(
            (ClientSize.Width - _bitmap.Width * scale) / 2 + _pan.X,
            (ClientSize.Height - _bitmap.Height * scale) / 2 + _pan.Y,
            _bitmap.Width * scale,
            _bitmap.Height * scale
        );
    }

    private Point ImagePoint(Point point)
    {
        var view = Viewport();
        return new Point(
            Math.Max(
                0,
                Math.Min(_bitmap!.Width, (int)Math.Round((point.X - view.X) * _bitmap.Width / view.Width))
            ),
            Math.Max(
                0,
                Math.Min(_bitmap.Height, (int)Math.Round((point.Y - view.Y) * _bitmap.Height / view.Height))
            )
        );
    }

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_bitmap == null)
        {
            e.Graphics.DrawString("DP.LabelInspection", Font, Brushes.SlateGray, 20, 20);
            return;
        }

        var view = Viewport();
        _renderer.Size = ClientSize;
        var viewport = _renderer.Viewport;
        viewport.Zoom((view.Width / _bitmap.Width) / viewport.Scale, new DP.Vision.PointD(0, 0));
        viewport.Pan(view.X - viewport.Origin.X, view.Y - viewport.Origin.Y);
        _renderer.RenderTo(e.Graphics);
        foreach (var region in _regions)
        {
            DrawLabel(
                e.Graphics,
                view,
                region.Bounds,
                region.Kind == ERegionKind.Ignore ? Color.Gray : Color.RoyalBlue,
                _findings.Any(f => f.Bounds.HasValue && f.Bounds.Value.Equals(region.Bounds))
                    ? ""
                    : region.Name
            );
        }

        foreach (var character in _characters)
        {
            DrawLabel(e.Graphics, view, character.Bounds, Color.ForestGreen, character.Character);
        }

        foreach (
            var group in _findings
                .Select((finding, index) => new { Finding = finding, Index = index + 1 })
                .Where(item => item.Finding.Bounds.HasValue)
                .GroupBy(item => item.Finding.Bounds!.Value)
        )
        {
            DrawLabel(
                e.Graphics,
                view,
                group.Key,
                group.Any(item => item.Finding.Verdict == EInspectionVerdict.Ng)
                    ? Color.Crimson
                    : Color.DarkOrange,
                ShowFindingLabels
                    ? string.Join(
                        "/",
                        group.Select(item =>
                            "F"
                            + item.Index
                            + (
                                item.Finding.Code == "barcode_summary"
                                    ? " " + item.Finding.Verdict.ToString().ToUpperInvariant()
                                    : ""
                            )
                        )
                    )
                    : ""
            );
        }

        if (EditRegions && _selected >= 0 && _selected < _regions.Count)
        {
            var box =
                _drawing && _editor.Tool == DP.Vision.UI.ERoiTool.Select && _editor.Preview != null
                    ? ToRect(_editor.Preview)
                    : _regions[_selected].Bounds;
            DrawBox(e.Graphics, view, box, Color.DarkViolet, "选中：拖动调整 · Delete删除");
            foreach (var handle in Handles(box))
            {
                e.Graphics.FillRectangle(Brushes.DarkViolet, handle.X - 4, handle.Y - 4, 8, 8);
            }
        }

        if (_drawing && _editor.Tool == DP.Vision.UI.ERoiTool.Rectangle && _editor.Preview != null)
        {
            DrawBox(e.Graphics, view, ToRect(_editor.Preview), Color.ForestGreen, "ROI");
        }

        ForegroundPaint?.Invoke(this, e);
    }

    private void DrawLabel(Graphics graphics, RectangleF viewport, PixelRect box, Color color, string caption)
    {
        if (string.IsNullOrEmpty(caption))
        {
            return;
        }

        float scale = viewport.Width / _bitmap!.Width;
        float x = viewport.X + box.X * scale,
            y = viewport.Y + box.Y * scale;
        if (x + box.Width * scale < 0 || y + box.Height * scale < 0 || x > Width || y > Height)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        graphics.DrawString(caption, Font, brush, x, Math.Max(0, y - Font.Height));
    }

    private void DrawBox(Graphics graphics, RectangleF viewport, PixelRect box, Color color, string caption)
    {
        float scale = viewport.Width / _bitmap!.Width;
        var rectangle = new RectangleF(
            viewport.X + box.X * scale,
            viewport.Y + box.Y * scale,
            box.Width * scale,
            box.Height * scale
        );
        if (
            rectangle.Right < 0
            || rectangle.Bottom < 0
            || rectangle.Left > ClientSize.Width
            || rectangle.Top > ClientSize.Height
        )
        {
            return;
        }

        using var pen = new Pen(color, 2);
        using var brush = new SolidBrush(color);
        graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        graphics.DrawString(caption, Font, brush, rectangle.X, Math.Max(0, rectangle.Y - Font.Height));
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_bitmap == null)
        {
            return;
        }

        Focus();
        if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            CancelGesture();
            _panStart = e.Location;
            _panOrigin = _pan;
            Capture = true;
            Cursor = Cursors.Hand;
            return;
        }

        if (
            !AllowRegionDrawing
            || e.Button != MouseButtons.Left
            || _panStart.HasValue
            || !Viewport().Contains(e.Location)
        )
        {
            return;
        }

        var point = ImagePoint(e.Location);
        var imagePoint = new DP.Vision.PointD(point.X, point.Y);
        // 7个屏幕像素的命中容差（原图单位）。
        double tolerance = 7.0 * _bitmap.Width / Viewport().Width;
        bool forceNew = (ModifierKeys & Keys.Shift) != 0;
        _pressAt = EditRegions ? e.Location : null;
        if (EditRegions && !forceNew)
        {
            _editor.Tool = DP.Vision.UI.ERoiTool.Select;
            int before = _selected;
            bool hit = _editor.PointerDown(imagePoint, tolerance);
            _selected = IndexOf(_editor.SelectedId);
            if (_selected != before)
            {
                SelectedRegionChanged?.Invoke(this, EventArgs.Empty);
            }

            if (hit)
            {
                _drawing = true;
                Capture = true;
                Invalidate();
                return;
            }

            if (!DrawOutsideRegions)
            {
                Invalidate();
                return;
            }
        }
        else if (!forceNew)
        {
            int hit = FindingAt(point, e.Location);
            if (hit >= 0)
            {
                FindingSelected?.Invoke(this, new FindingSelectedEventArgs(hit));
                return;
            }
        }

        _editor.Tool = DP.Vision.UI.ERoiTool.Rectangle;
        _editor.PointerDown(imagePoint, tolerance);
        _drawing = true;
        Capture = true;
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panStart.HasValue)
        {
            _pan = new PointF(_panOrigin.X + e.X - _panStart.Value.X, _panOrigin.Y + e.Y - _panStart.Value.Y);
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_drawing)
        {
            var point = ImagePoint(e.Location);
            _editor.PointerMove(new DP.Vision.PointD(point.X, point.Y));
            Invalidate();
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panStart.HasValue)
        {
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _pan = new PointF(
                    _panOrigin.X + e.X - _panStart.Value.X,
                    _panOrigin.Y + e.Y - _panStart.Value.Y
                );
                CancelGesture();
                Invalidate();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var pressAt = _pressAt;
        _pressAt = null;
        if (e.Button != MouseButtons.Left || !_drawing)
        {
            return;
        }

        var point = ImagePoint(e.Location);
        var before = _editor.Document;
        _editor.PointerUp(new DP.Vision.PointD(point.X, point.Y));
        var after = _editor.Document;
        _drawing = false;
        Capture = false;
        if (ReferenceEquals(before, after) && pressAt is Point press
            && Math.Abs(press.X - e.X) <= 3 && Math.Abs(press.Y - e.Y) <= 3)
        {
            // 编辑模式下单击（未拖动）且落在检测证据上时仍选中证据，与非编辑模式一致。
            int hit = FindingAt(point, e.Location);
            if (hit >= 0)
            {
                FindingSelected?.Invoke(this, new FindingSelectedEventArgs(hit));
            }
        }
        else if (!ReferenceEquals(before, after))
        {
            // 新建：文档中多出的ROI；编辑：同一标识的几何改变。均先复原为宿主的配方区域，再由宿主通过事件提交修改。
            var created = after.Rois.FirstOrDefault(r => !before.Rois.Any(b => b.Id == r.Id));
            var edited = after.Rois.FirstOrDefault(r =>
                before.Rois.Any(b => b.Id == r.Id && !ReferenceEquals(b, r))
            );
            LoadEditor();
            if (created != null)
            {
                RegionDrawn?.Invoke(this, new RegionDrawnEventArgs(ToRect(created.Shape)));
            }
            else if (edited != null && IndexOf(edited.Id) is int index && index >= 0)
            {
                RegionEdited?.Invoke(this, new RegionEditedEventArgs(index, ToRect(edited.Shape)));
            }
        }

        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _panStart = null;
            Cursor = Cursors.Default;
            if (_drawing)
            {
                _drawing = false;
                _editor.Cancel();
            }

            Invalidate();
        }
    }

    // 命中的最小检测证据序号；无命中为-1。
    private int FindingAt(Point image, Point screen) =>
        Enumerable
            .Range(0, _findings.Count)
            .Where(i => _findings[i].Bounds.HasValue && FindingHit(_findings[i].Bounds!.Value, image, screen))
            .OrderBy(i => (long)_findings[i].Bounds!.Value.Width * _findings[i].Bounds!.Value.Height)
            .DefaultIfEmpty(-1)
            .First();

        private bool FindingHit(PixelRect box, Point image, Point screen)
    {
        var v = Viewport();
        float s = v.Width / _bitmap!.Width;
        var r = new RectangleF(v.X + box.X * s, v.Y + box.Y * s, box.Width * s, box.Height * s);
        if (r.Right < 0 || r.Bottom < 0 || r.Left > Width || r.Top > Height)
        {
            return false;
        }

        return Contains(box, image)
            || (
                ShowFindingLabels
                && new RectangleF(r.X, Math.Max(0, r.Y - Font.Height), 60, Font.Height).Contains(screen)
            );
    }

    private static bool Contains(PixelRect box, Point p)
    {
        return p.X >= box.X && p.Y >= box.Y && p.X <= box.X + box.Width && p.Y <= box.Y + box.Height;
    }

    private PointF[] Handles(PixelRect box)
    {
        var v = Viewport();
        float s = v.Width / _bitmap!.Width,
            l = v.X + box.X * s,
            t = v.Y + box.Y * s,
            r = l + box.Width * s,
            b = t + box.Height * s;
        return new[]
        {
            new PointF(l, t),
            new PointF((l + r) / 2, t),
            new PointF(r, t),
            new PointF(r, (t + b) / 2),
            new PointF(r, b),
            new PointF((l + r) / 2, b),
            new PointF(l, b),
            new PointF(l, (t + b) / 2),
        };
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            CancelGesture();
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Home)
        {
            FitToWindow();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
        {
            ZoomAt(1.25f, new Point(Width / 2, Height / 2));
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
        {
            ZoomAt(.8f, new Point(Width / 2, Height / 2));
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) == Keys.Home || base.IsInputKey(keyData);
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt((float)Math.Pow(1.2, e.Delta / 120.0), e.Location);
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (CanFocus)
        {
            Focus();
        }
    }

    /// <inheritdoc/>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer.Dispose();
            _source?.Dispose();
            _source = null;
            _geometry = null;
            _bitmap = null;
        }

        base.Dispose(disposing);
    }
}
