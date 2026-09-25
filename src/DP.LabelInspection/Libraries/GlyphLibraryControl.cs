using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>可嵌入的独立字符编辑器；宿主注入管理器，不依赖具体存储或视觉实现。</summary>
public sealed class GlyphLibraryControl : UserControl
{
    private readonly ComboBox _libraries = new ComboBox
    {
        Width = 310,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly NumericUpDown _revision = new NumericUpDown
    {
        Minimum = 1,
        Maximum = 1000000,
        Width = 75,
    };
    private readonly TextBox _character = new TextBox { Width = 40, MaxLength = 1 };
    private readonly ComboBox _mode = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ImageViewerControl _viewer = new ImageViewerControl { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _gallery = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
    };
    private readonly Label _status = new Label
    {
        AutoSize = true,
        Text = "单字是参考单位；常规/窄体分别建库，来源图不限制组合。",
    };
    private IGlyphCandidateService? _candidateService;
    private IGlyphLibraryManager? _manager;
    private ImageFrame? _candidate;
    private PixelRect? _crop;
    private string? _provenance;

    /// <summary>创建无参数、可安全用于设计器的编辑器。</summary>
    public GlyphLibraryControl()
    {
        Size = new Size(960, 650);
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        _mode.Items.AddRange(new object[] { "otsu", "fixed", "midpoint" });
        _mode.SelectedIndex = 0;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange(new Control[] { _libraries, _revision });
        Add(
            top,
            "多图补录 / 手工分割",
            () =>
            {
                var id = (_libraries.SelectedItem as GlyphLibraryInfo)?.Id;
                GlyphQuickBuilderControl.ShowPage(
                    FindForm(),
                    Manager,
                    _candidateService,
                    selectedLibrary: id
                );
                Reload(id);
            }
        );
        Add(top, "刷新", () => Reload());
        Add(top, "读取版本", () => ShowLibrary());
        Add(
            top,
            "新建类别",
            () =>
            {
                string? name = EditorDialogs.Ask("类别名称（常规/窄体等）", "");
                if (name != null)
                {
                    Reload(Manager.CreateLibrary(name));
                }
            }
        );
        Add(
            top,
            "导入JSON",
            () =>
            {
                using var d = new OpenFileDialog { Filter = "字库|*.json" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    Reload(Manager.ImportLibrary(File.ReadAllText(d.FileName)));
                }
            }
        );
        Add(
            top,
            "导出JSON",
            () =>
            {
                var head = Selected;
                using var d = new SaveFileDialog
                {
                    Filter = "字库|*.json",
                    FileName = head.Id + "-r" + _revision.Value + ".json",
                };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(d.FileName, Manager.ExportLibrary(head.Id, (int)_revision.Value));
                }
            }
        );
        Add(
            top,
            "归档/恢复",
            () =>
            {
                var head = Selected;
                Manager.Archive(head.Id, (int)_revision.Value, !head.Archived);
                Reload(head.Id);
            }
        );
        var edit = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        edit.Controls.Add(new Label { Text = "独立字符：", AutoSize = true });
        edit.Controls.Add(_character);
        edit.Controls.Add(_mode);
        Add(
            edit,
            "载入裁剪图",
            () =>
            {
                using var d = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg" };
                if (d.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                using var image = Image.FromFile(d.FileName);
                SetCandidate(DrawingImageConverter.FromImage(image), _character.Text);
            }
        );
        Add(
            edit,
            "确认单字并保存新版本",
            () =>
            {
                if (_candidate == null)
                {
                    throw new InvalidOperationException("先载入图像或从检测结果选取单字。");
                }

                var image = _crop.HasValue ? _candidate.Crop(_crop.Value) : _candidate;
                if (
                    MessageBox.Show(
                        "确认这个单字标签、字形类别及参考外观？不会替换其他字符或重排组合。",
                        "确认候选参考",
                        MessageBoxButtons.YesNo
                    ) != DialogResult.Yes
                )
                {
                    return;
                }

                var head = Selected;
                Manager.PutGlyph(
                    head.Id,
                    (int)_revision.Value,
                    _character.Text,
                    image,
                    (string)_mode.SelectedItem!,
                    _provenance
                );
                Reload(head.Id);
            }
        );
        Add(
            edit,
            "规则字表导入",
            () =>
            {
                if (_candidate == null)
                {
                    throw new InvalidOperationException("先载入字表图。");
                }

                string? alphabet = EditorDialogs.Ask(
                    "字表：按从左到右、从上到下填写独立字符（不可重复）",
                    ""
                );
                if (alphabet == null)
                {
                    return;
                }

                string? shape = EditorDialogs.Ask("行数,列数,单元内边距", "1," + alphabet.Length + ",1");
                if (shape == null)
                {
                    return;
                }

                var p = shape.Split(',');
                if (p.Length != 3)
                {
                    throw new ArgumentException("需要行、列、内边距。");
                }

                var head = Selected;
                Manager.PutSheet(
                    head.Id,
                    (int)_revision.Value,
                    _candidate,
                    alphabet,
                    int.Parse(p[0]),
                    int.Parse(p[1]),
                    int.Parse(p[2]),
                    (string)_mode.SelectedItem!
                );
                Reload(head.Id);
            }
        );
        Add(
            edit,
            "删除单字",
            () =>
            {
                var head = Selected;
                if (
                    MessageBox.Show("仅在新版本删除该字符，历史保留？", "删除", MessageBoxButtons.YesNo)
                    == DialogResult.Yes
                )
                {
                    Manager.RemoveGlyph(head.Id, (int)_revision.Value, _character.Text);
                    Reload(head.Id);
                }
            }
        );
        var split = new SplitContainer
        {
            Size = new Size(900, 600),
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 260,
        };
        split.Panel1.Controls.Add(_viewer);
        split.Panel2.Controls.Add(_gallery);
        Controls.Add(split);
        Controls.Add(_status);
        _status.Dock = DockStyle.Bottom;
        Controls.Add(edit);
        Controls.Add(top);
        _libraries.SelectedIndexChanged += (_, _) =>
        {
            if (_libraries.SelectedItem is GlyphLibraryInfo h)
            {
                _revision.Value = h.Revision;
                ShowLibrary();
            }
        };
        _viewer.RegionDrawn += (_, e) =>
        {
            _crop = e.Bounds;
            _viewer.SetOverlays(
                new[] { new InspectionRegion("crop", ERegionKind.Ignore, e.Bounds) },
                Array.Empty<InspectionFinding>()
            );
            _status.Text = "选中 " + e.Bounds + "；必须是一个完整字符，4–512像素。";
        };
    }

    /// <summary>连接宿主拥有的管理器，并加载各类别最新版本信息。</summary>
    /// <param name = "manager">宿主拥有的字库管理器，控件不负责释放。</param>
    public void AttachManager(IGlyphLibraryManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        Reload();
    }

    /// <summary>为快捷参考制作连接串行OCR/分割服务，不转移服务所有权。</summary>
    /// <param name = "service">宿主拥有的串行候选提取服务。</param>
    public void AttachCandidateService(IGlyphCandidateService service)
    {
        _candidateService = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>从检测图集或宿主加载候选图块，仅在用户明确确认后保存。</summary>
    /// <param name = "frame">独立不可变候选图像。</param>
    /// <param name = "character">候选字符标签，发布前须人工确认。</param>
    /// <param name = "provenanceJson">可选的来源证据JSON。</param>
    public void SetCandidate(ImageFrame frame, string character, string? provenanceJson = null)
    {
        _candidate = frame;
        _crop = null;
        _provenance = provenanceJson;
        _character.Text = character;
        _viewer.SetImage(frame);
        _viewer.SetOverlays(Array.Empty<InspectionRegion>(), Array.Empty<InspectionFinding>());
    }

    private IGlyphLibraryManager Manager =>
        _manager ?? throw new InvalidOperationException("未连接字库管理器。");
    private GlyphLibraryInfo Selected =>
        _libraries.SelectedItem as GlyphLibraryInfo ?? throw new InvalidOperationException("请选择类别。");

    private void Reload(string? id = null)
    {
        if (_manager == null)
        {
            return;
        }

        var items = Manager.ListLibraries(true);
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

    private void ShowLibrary()
    {
        if (_manager == null || _libraries.SelectedItem == null)
        {
            return;
        }

        try
        {
            var library = Manager.Load(Selected.Id, (int)_revision.Value);
            ClearGallery();
            foreach (var entry in library.Glyphs.Values.OrderBy(g => g.Character, StringComparer.Ordinal))
            {
                var panel = new FlowLayoutPanel
                {
                    Width = 100,
                    Height = 130,
                    FlowDirection = FlowDirection.TopDown,
                };
                var picture = new PictureBox
                {
                    Width = 90,
                    Height = 85,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Image = DrawingImageConverter.ToBitmap(entry.Image),
                };
                picture.Click += (_, _) =>
                {
                    SetCandidate(entry.Image, entry.Character);
                    _mode.SelectedItem = entry.Binarization;
                };
                panel.Controls.Add(picture);
                panel.Controls.Add(
                    new Label
                    {
                        Text = entry.Character + " · " + entry.Image.Width + "×" + entry.Image.Height,
                        AutoSize = true,
                    }
                );
                _gallery.Controls.Add(panel);
            }

            _status.Text =
                library.Name
                + " r"
                + library.Revision
                + " · "
                + library.Glyphs.Count
                + "个独立字符；历史版本可读，编辑基于旧版本将被拒绝。";
        }
        catch (Exception error)
        {
            _status.Text = error.Message;
        }
    }

    private void ClearGallery()
    {
        foreach (Control panel in _gallery.Controls.Cast<Control>().ToArray())
        {
            foreach (var picture in panel.Controls.OfType<PictureBox>())
            {
                picture.Image?.Dispose();
            }

            panel.Dispose();
        }

        _gallery.Controls.Clear();
    }

    private static void Add(Control parent, string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "字库操作未完成");
            }
        };
        parent.Controls.Add(b);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearGallery();
        }

        base.Dispose(disposing);
    }
}
