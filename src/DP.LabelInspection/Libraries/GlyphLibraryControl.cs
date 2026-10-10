using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>单字库浏览器：左侧单字缩略图，右侧只读放大预览；新增及编辑统一由制库页完成。</summary>
public sealed class GlyphLibraryControl : UserControl
{
    private readonly ModernSelect _libraries = new ModernSelect { Width = 260, Theme = ModernTheme.Dark };
    private readonly ModernToolStrip _toolbar = new ModernToolStrip { Dock = DockStyle.Top, Theme = ModernTheme.Dark };
    private readonly ImageViewerControl _viewer = new ImageViewerControl
    {
        Dock = DockStyle.Fill,
        AllowRegionDrawing = false,
    };
    private readonly FlowLayoutPanel _gallery = new FlowLayoutPanel
    {
        // 由滚动视图约束宽度并测量高度；AutoSize 会把横向内容撑成一整行，阻止换行。
        AutoSize = false,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Padding = new Padding(6),
    };
    private readonly Label _previewTitle = new Label
    {
        Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 8, 12, 4),
        Text = "选择左侧单字查看放大图", AutoEllipsis = true,
    };
    private readonly Label _status = new Label
    {
        Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 4, 8, 4), AutoEllipsis = true,
        Text = "左侧浏览单字，右侧放大预览；新增和编辑请切换到多图制库。",
    };
    private readonly ToolStripButton _deleteGlyph;
    private readonly ToolStripButton _deleteLibrary;
    private readonly ToolStripButton _export;
    private IGlyphLibraryManager? _manager;
    private GlyphReference? _selectedGlyph;
    private bool _syncing;

    /// <summary>创建无参数、可安全用于设计器的单字浏览器。</summary>
    public GlyphLibraryControl()
    {
        Size = new Size(960, 650);
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        _toolbar.Items.Add(new ToolStripLabel("字库"));
        _toolbar.Items.Add(new ToolStripControlHost(_libraries) { AutoSize = false, Size = _libraries.Size, ToolTipText = "字库" });
        _toolbar.Items.Add(new ToolStripSeparator());
        Command("刷新", ModernIconKind.Refresh, () => RefreshLibraries());
        Command("导入JSON", ModernIconKind.FolderOpen, ImportLibrary);
        _export = Command("导出JSON", ModernIconKind.SaveAs, ExportLibrary);
        _toolbar.Items.Add(new ToolStripSeparator());
        _deleteGlyph = Command("删除单字", ModernIconKind.Close, ConfirmDeleteGlyph);
        _deleteLibrary = Command("删除字库", ModernIconKind.Close, ConfirmDeleteLibrary);
        _deleteLibrary.ToolTipText = "从可选列表移除当前字库；保留历史版本供旧配方读取";
        _toolbar.Items.Add(new ToolStripSeparator());
        Command("适应窗口", ModernIconKind.FitWindow, _viewer.FitToWindow);
        Command("1:1", ModernIconKind.Eye, _viewer.ActualSize);

        var preview = new Panel { Dock = DockStyle.Fill };
        preview.Controls.Add(_viewer);
        preview.Controls.Add(_previewTitle);
        var split = new ModernSplitter
        {
            Size = new Size(960, 600),
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            InitialPanel2Size = 0,
            Panel1MinSize = 220,
            Panel2MinSize = 250,
            SplitterDistance = 400,
        };
        split.Panel1.Controls.Add(new ModernScrollView { Dock = DockStyle.Fill, Content = _gallery });
        split.Panel2.Controls.Add(preview);
        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        _libraries.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing) ShowLibrary();
        };
        InspectionUiStyle.Apply(this);
        InspectionUiStyle.FitToolStrip(_toolbar);
        UpdateCommands();
    }

    /// <summary>获取当前选中字库标识；无字库时为空。</summary>
    public string? SelectedLibraryId => (_libraries.SelectedItem as GlyphLibraryInfo)?.Id;

    /// <summary>获取当前放大预览的单字身份；未选择时为空。</summary>
    public string? SelectedCharacter => _selectedGlyph?.Character;

    /// <summary>连接宿主拥有的管理器，保留可用的当前选择并刷新最新版本。</summary>
    /// <param name="manager">宿主拥有的管理器，控件不负责释放。</param>
    public void AttachManager(IGlyphLibraryManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        RefreshLibraries();
    }

    /// <summary>保留宿主兼容入口；候选提取服务仅由制库页使用，浏览器不执行编辑。</summary>
    /// <param name="service">宿主拥有的候选提取服务。</param>
    public void AttachCandidateService(IGlyphCandidateService service)
    {
        if (service == null) throw new ArgumentNullException(nameof(service));
    }

    /// <summary>只读预览宿主提供的候选图块；不创建或保存参考。</summary>
    /// <param name="frame">独立不可变候选图像。</param>
    /// <param name="character">候选单字标签。</param>
    /// <param name="provenanceJson">保留兼容参数，浏览器不会发布来源记录。</param>
    public void SetCandidate(PixelSnapshot frame, string character, string? provenanceJson = null)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        _selectedGlyph = null;
        _viewer.SetImage(frame);
        _previewTitle.Text = character + " · 候选预览；编辑请使用多图制库";
        UpdateCommands();
    }

    /// <summary>刷新当前活动字库；优先保留指定类别和当前单字，失效时选择第一个可用项。</summary>
    /// <param name="selectedLibrary">要选中的类别标识，null保留当前类别。</param>
    public void RefreshLibraries(string? selectedLibrary = null)
    {
        if (_manager == null) return;
        string? id = selectedLibrary ?? SelectedLibraryId;
        var heads = Manager.ListLibraries(false);
        _syncing = true;
        try
        {
            _libraries.Items.Clear();
            foreach (var head in heads) _libraries.Items.Add(head);
            _libraries.SelectedItem = heads.FirstOrDefault(h => h.Id == id) ?? heads.FirstOrDefault();
        }
        finally { _syncing = false; }
        ShowLibrary();
    }

    /// <summary>从当前最新版本移除选中的单字，保留所有历史修订；调用方负责用户确认。</summary>
    public void DeleteSelectedGlyph()
    {
        var head = Selected;
        string character = SelectedCharacter ?? throw new InvalidOperationException("请先选中左侧单字。");
        Manager.RemoveGlyph(head.Id, head.Revision, character);
        RefreshLibraries(head.Id);
    }

    /// <summary>从活动字库列表移除当前类别，保留旧配方所引用的历史修订；调用方负责用户确认。</summary>
    public void DeleteSelectedLibrary()
    {
        var head = Selected;
        Manager.Archive(head.Id, head.Revision);
        RefreshLibraries();
    }

    private IGlyphLibraryManager Manager => _manager ?? throw new InvalidOperationException("未连接字库管理器。");
    private GlyphLibraryInfo Selected => _libraries.SelectedItem as GlyphLibraryInfo
        ?? throw new InvalidOperationException("请选择字库。");

    private void ShowLibrary()
    {
        string? character = SelectedCharacter;
        ClearGallery();
        _selectedGlyph = null;
        _viewer.SetImage(null);
        _previewTitle.Text = "选择左侧单字查看放大图";
        try
        {
            if (_manager == null || _libraries.SelectedItem is not GlyphLibraryInfo head)
            {
                _status.Text = "暂无字库；请切换到多图制库创建，或在工具栏导入JSON。";
                return;
            }
            var library = Manager.Load(head.Id, head.Revision);
            foreach (var entry in library.Glyphs.Values.OrderBy(g => g.Character, StringComparer.Ordinal))
            {
                var card = new ModernPanel
                {
                    Size = new Size(112, 138), Padding = new Padding(6), Cursor = Cursors.Hand,
                    Tag = entry, Theme = ModernTheme.Dark,
                };
                var picture = new PictureBox
                {
                    Dock = DockStyle.Top, Height = 96, SizeMode = PictureBoxSizeMode.Zoom,
                    Image = DrawingImageConverter.ToBitmap(entry.Image), Cursor = Cursors.Hand,
                    AccessibleName = entry.Character,
                };
                var caption = new Label
                {
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Text = entry.Character + " · " + entry.Image.Width + "×" + entry.Image.Height,
                    AutoEllipsis = true,
                    ForeColor = ModernTheme.Dark.Text, BackColor = ModernTheme.Dark.Container,
                    Cursor = Cursors.Hand,
                };
                card.Click += (_, _) => SelectGlyph(entry);
                picture.Click += (_, _) => SelectGlyph(entry);
                caption.Click += (_, _) => SelectGlyph(entry);
                card.Controls.Add(caption);
                card.Controls.Add(picture);
                _gallery.Controls.Add(card);
            }
            var selected = library.Glyphs.Values.FirstOrDefault(g => g.Character == character)
                ?? library.Glyphs.Values.OrderBy(g => g.Character, StringComparer.Ordinal).FirstOrDefault();
            if (selected != null) SelectGlyph(selected);
            _status.Text = library.Name + " r" + library.Revision + " · " + library.Glyphs.Count
                + " 个单字；左侧选择、右侧放大；编辑统一在多图制库中完成。";
        }
        catch (Exception error) { _status.Text = error.Message; }
        finally { UpdateCommands(); }
    }

    private void SelectGlyph(GlyphReference glyph)
    {
        _selectedGlyph = glyph;
        _viewer.SetImage(glyph.Image);
        _previewTitle.Text = glyph.Character + " · " + glyph.Image.Width + " × " + glyph.Image.Height
            + " · " + glyph.Binarization;
        foreach (ModernPanel card in _gallery.Controls)
            foreach (var caption in card.Controls.OfType<Label>())
                caption.ForeColor = ReferenceEquals(card.Tag, glyph) ? ModernTheme.Dark.Primary : ModernTheme.Dark.Text;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        _export.Enabled = _deleteLibrary.Enabled = SelectedLibraryId != null;
        _deleteGlyph.Enabled = _selectedGlyph != null;
    }

    private void ConfirmDeleteGlyph()
    {
        if (MessageBox.Show(FindForm(), "删除选中的单字“" + SelectedCharacter
            + "”？将生成新修订，旧版本和旧配方绑定保持不变。", "删除单字",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            DeleteSelectedGlyph();
    }

    private void ConfirmDeleteLibrary()
    {
        if (MessageBox.Show(FindForm(), "将字库“" + Selected.Name
            + "”从可选列表移除？历史版本仍保留，避免破坏旧配方绑定。", "删除字库",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            DeleteSelectedLibrary();
    }

    private void ImportLibrary()
    {
        using var dialog = new OpenFileDialog { Filter = "字库|*.json" };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            RefreshLibraries(Manager.ImportLibrary(File.ReadAllText(dialog.FileName)));
    }

    private void ExportLibrary()
    {
        var head = Selected;
        using var dialog = new SaveFileDialog { Filter = "字库|*.json", FileName = head.Id + "-r" + head.Revision + ".json" };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, Manager.ExportLibrary(head.Id, head.Revision));
    }

    private ToolStripButton Command(string text, ModernIconKind icon, Action action)
    {
        var button = new ToolStripButton(text, ModernIcons.CreateBitmap(icon, ModernTheme.Dark.Text))
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = text,
        };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(FindForm(), error.Message, "字库操作未完成"); }
        };
        _toolbar.Items.Add(button);
        return button;
    }

    private void ClearGallery()
    {
        foreach (Control card in _gallery.Controls.Cast<Control>().ToArray())
        {
            foreach (var picture in card.Controls.OfType<PictureBox>()) picture.Image?.Dispose();
            card.Dispose();
        }
        _gallery.Controls.Clear();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearGallery();
            foreach (ToolStripItem item in _toolbar.Items) item.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
