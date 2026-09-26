using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>
/// 逐字符异常模型（质量方法B）的制作页，流程与单字库多图制库相同：载入多张良品图，按文字ROI用OCR/分割提取字符候选，
/// 人工核对身份并勾选样本，按字符查看样本数量，训练后作为一个新版本发布到异常模型库。
/// 与单字库不同，每个字符保留多个样本（建议每字至少3个、越多越稳），模型学习正常印刷波动。
/// </summary>
public sealed partial class CharacterAnomalyBuilderControl : UserControl
{
    private const int SuggestedSamples = 3;
    private readonly ComboBox _libraries = new ComboBox
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly ComboBox _regions = new ComboBox
    {
        Width = 200,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly TextBox _confirmed = new TextBox { Width = 220 };
    private readonly ListBox _images = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ImageViewerControl _viewer = new ImageViewerControl { Dock = DockStyle.Fill };
    private readonly DataGridView _samples = new DataGridView
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
    };
    private readonly ListView _coverage = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    private readonly Label _status = new Label
    {
        AutoSize = true,
        Text = "载入良品图 → 选择文字ROI → 提取字符 → 核对身份/勾选 → 训练并发布。",
    };
    private readonly List<(ImageFrame Image, string Name)> _sources = new List<(ImageFrame, string)>();
    private readonly List<LineCandidate> _lines = new List<LineCandidate>();

    /// <summary>
    /// 每张图上各文字ROI的框（原图坐标）；没有记录时使用配方位置。逐字符模型按行几何归一化，与框在图上的位置无关，
    /// 因此良品图不必与配方对齐，框只需在每张图上完整框住这一行文字。
    /// </summary>
    private readonly Dictionary<(ImageFrame Image, string Region), PixelRect> _boxes =
        new Dictionary<(ImageFrame, string), PixelRect>();
    private readonly Label _hint = new Label
    {
        AutoSize = true,
        Margin = new Padding(8, 7, 0, 0),
        ForeColor = Color.FromArgb(80, 80, 80),
        Text = "拖动框的边/角调整、拖动框内部移动，在框外拖动重新画框；框只影响本图的样本提取。",
    };
    private readonly List<Control> _busyDisabled = new List<Control>();
    private IAnomalyLibraryManager? _manager;
    private IAnomalyModelTrainer? _trainer;
    private IGlyphCandidateService? _candidates;
    private bool _filling;

    /// <summary>创建无参数、可安全用于设计器的制作页。</summary>
    public CharacterAnomalyBuilderControl()
    {
        Size = new Size(1100, 720);
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        _samples.Columns.Add(
            new DataGridViewImageColumn
            {
                Name = "Patch",
                HeaderText = "字符图",
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                Width = 60,
            }
        );
        _samples.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Label",
                HeaderText = "身份",
                Width = 50,
            }
        );
        _samples.Columns.Add(
            new DataGridViewCheckBoxColumn
            {
                Name = "Use",
                HeaderText = "作样本",
                Width = 55,
            }
        );
        _samples.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = "Source",
                HeaderText = "来源",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            }
        );
        _samples.Columns["Patch"]!.ReadOnly = true;
        _samples.CellValueChanged += (_, e) => CellChanged(e.RowIndex, e.ColumnIndex);
        _samples.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_samples.IsCurrentCellDirty && _samples.CurrentCell is DataGridViewCheckBoxCell)
            {
                _samples.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _coverage.Columns.Add("字符", 50);
        _coverage.Columns.Add("样本数", 60);
        _coverage.Columns.Add("说明", 110);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.Add(
            new Label
            {
                Text = "异常模型库：",
                AutoSize = true,
                Margin = new Padding(3, 7, 0, 0),
            }
        );
        top.Controls.Add(_libraries);
        Add(top, "刷新", () => Reload(Head?.Id));
        Add(
            top,
            "新建字符模型库",
            () =>
            {
                string? name = EditorDialogs.Ask("模型库名称（字体/产品等）", "");
                if (name != null)
                {
                    Reload(Manager.CreateAnomalyLibrary(name));
                }
            }
        );
        var extract = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        Add(
            extract,
            "添加良品图…",
            () =>
            {
                using var d = new OpenFileDialog
                {
                    Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg",
                    Multiselect = true,
                };
                if (d.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                foreach (string file in d.FileNames)
                {
                    using var image = Image.FromFile(file);
                    AddImage(DrawingImageConverter.FromImage(image), Path.GetFileName(file));
                }
            }
        );
        extract.Controls.Add(
            new Label
            {
                Text = "文字ROI：",
                AutoSize = true,
                Margin = new Padding(8, 7, 0, 0),
            }
        );
        extract.Controls.Add(_regions);
        extract.Controls.Add(
            new Label
            {
                Text = "确认文本（可空=OCR）：",
                AutoSize = true,
                Margin = new Padding(8, 7, 0, 0),
            }
        );
        extract.Controls.Add(_confirmed);
        Add(
            extract,
            "当前框用于全部图像",
            () =>
            {
                var region = SelectedRegion ?? throw new InvalidOperationException("请先选择或画出文字ROI。");
                var image = CurrentImage ?? throw new InvalidOperationException("请选择图像。");
                var box = BoxFor(image, region);
                foreach (var (other, _) in _sources)
                {
                    SetBox(other, region, box, quiet: true);
                }

                ShowImage();
                _status.Text = "已把当前框用于全部图像；受影响图像的旧样本已清除，请重新提取。";
            }
        );
        Add(
            extract,
            "本图恢复配方位置",
            () =>
            {
                var region = SelectedRegion ?? throw new InvalidOperationException("请先选择文字ROI。");
                var image = CurrentImage ?? throw new InvalidOperationException("请选择图像。");
                if (region.AdHoc)
                {
                    throw new InvalidOperationException("手画的文字行没有配方位置。");
                }

                SetBox(image, region, region.Region.Bounds);
            }
        );
        Add(extract, "提取所选图像", async () => await ExtractAsync(selectedOnly: true));
        Add(extract, "提取全部图像", async () => await ExtractAsync(selectedOnly: false));
        Add(
            extract,
            "清空样本",
            () =>
            {
                _lines.Clear();
                RefreshSamples();
            }
        );

        extract.Controls.Add(_hint);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        Add(bottom, "训练并发布新版本", async () => await TrainAsync());
        bottom.Controls.Add(_status);
        _busyDisabled.AddRange(new Control[] { top, extract, bottom, _samples });

        // 固定宽度的停靠布局（SplitContainer的分隔位置在控件尚无实际尺寸时设置会失效，画布被挤成一条）。
        _images.Dock = DockStyle.Left;
        _images.Width = 170;
        _coverage.Dock = DockStyle.Right;
        _coverage.Width = 230;
        _samples.Dock = DockStyle.Bottom;
        _samples.Height = 250;
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_viewer);
        center.Controls.Add(_samples);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(center);
        left.Controls.Add(_coverage);
        left.Controls.Add(_images);
        Controls.Add(left);
        Controls.Add(extract);
        Controls.Add(top);
        Controls.Add(bottom);
        _images.SelectedIndexChanged += (_, _) => ShowImage();
        _regions.SelectedIndexChanged += (_, _) => ShowImage();
        _viewer.EditRegions = true;
        _viewer.DrawOutsideRegions = true;
        _viewer.RegionEdited += (_, e) => TryUi(() => BoxChanged(e.Bounds));
        _viewer.RegionDrawn += (_, e) => TryUi(() => BoxChanged(e.Bounds));
    }

    private RegionChoice? SelectedRegion => _regions.SelectedItem as RegionChoice;

    private ImageFrame? CurrentImage =>
        _images.SelectedIndex >= 0 && _images.SelectedIndex < _sources.Count
            ? _sources[_images.SelectedIndex].Image
            : null;

    /// <summary>该图上该文字ROI的框：手动调整过的用调整后的，否则用配方位置。</summary>
    private PixelRect BoxFor(ImageFrame image, RegionChoice region)
    {
        return _boxes.TryGetValue((image, region.Name), out var box) ? box : region.Region.Bounds;
    }

    /// <summary>在画布上调整或重画了框：没有文字ROI时新建一个手画文字行，否则更新所选ROI在当前图上的框。</summary>
    private void BoxChanged(PixelRect bounds)
    {
        if (Busy)
        {
            return;
        }

        var image = CurrentImage ?? throw new InvalidOperationException("请先添加并选择图像。");
        var region = SelectedRegion;
        if (region == null)
        {
            int n = 1;
            while (_regions.Items.Cast<RegionChoice>().Any(r => r.Name == "文字行" + n))
            {
                n++;
            }

            region = new RegionChoice(
                new InspectionRegion("文字行" + n, ERegionKind.Text, bounds, singleLine: true),
                adHoc: true
            );
            _regions.Items.Add(region);
            _regions.SelectedItem = region;
        }

        SetBox(image, region, bounds);
    }

    private void SetBox(ImageFrame image, RegionChoice region, PixelRect bounds, bool quiet = false)
    {
        if (!bounds.Fits(image) || bounds.Width < 8 || bounds.Height < 8)
        {
            throw new ArgumentException("框须在图像内且至少8×8像素。");
        }

        bool changed = !BoxFor(image, region).Equals(bounds);
        _boxes[(image, region.Name)] = bounds;
        if (changed)
        {
            // 框变了，按旧框提取的样本已不对应，清除后需重新提取。
            _lines.RemoveAll(l => ReferenceEquals(l.Image, image) && l.Region == region.Name);
            RefreshSamples();
        }

        if (!quiet)
        {
            ShowImage();
            _status.Text = changed
                ? $"已调整本图“{region.Name}”的框（{bounds.Width}×{bounds.Height}）；本图此ROI的旧样本已清除，请重新提取。"
                : "框未变化。";
        }
    }

    private bool Busy => UseWaitCursor;

    private void TryUi(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "字符异常模型制作未完成");
            ShowImage();
        }
    }

    /// <summary>最近一次发布的模型库、版本及参与制作的文字ROI名称；宿主可据此把ROI绑定为逐字符模式。</summary>
    public (string LibraryId, int Revision, IReadOnlyList<string> Regions)? LastPublished
    {
        get;
        private set;
    }

    /// <summary>当前全部行候选中作为样本的字符数。</summary>
    public int SampleCount => _lines.Sum(l => l.Include.Count(i => i));

    /// <summary>连接宿主拥有的服务：模型库管理器、训练实现及字符候选提取（OCR+分割，与单字库制库共用）。</summary>
    /// <param name = "manager">异常模型库管理器。</param>
    /// <param name = "trainer">训练实现。</param>
    /// <param name = "candidates">字符候选提取服务；没有时无法提取。</param>
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

    /// <summary>设置可选的文字ROI（配方坐标，须为横向单行）。</summary>
    /// <param name = "regions">当前配方的ROI，只列出文字ROI。</param>
    public void SetRegions(IEnumerable<InspectionRegion> regions)
    {
        _regions.Items.Clear();
        foreach (
            var r in (regions ?? throw new ArgumentNullException(nameof(regions))).Where(r =>
                r.Kind == ERegionKind.Text
            )
        )
        {
            _regions.Items.Add(new RegionChoice(r));
        }

        if (_regions.Items.Count > 0)
        {
            _regions.SelectedIndex = 0;
        }
    }

    /// <summary>加入一张良品整图。</summary>
    /// <param name = "image">独立不可变整图。</param>
    /// <param name = "name">显示名称。</param>
    public void AddImage(ImageFrame image, string name)
    {
        _sources.Add((image ?? throw new ArgumentNullException(nameof(image)), name ?? "图像"));
        _images.Items.Add(name ?? "图像");
        _images.SelectedIndex = _images.Items.Count - 1;
    }

    /// <summary>对图像提取当前文字ROI的字符候选（OCR身份或确认文本），追加为行候选。</summary>
    /// <param name = "selectedOnly">只提取所选图像；false时提取全部图像。</param>
    public async Task ExtractAsync(bool selectedOnly)
    {
        var service =
            _candidates
            ?? throw new InvalidOperationException("宿主未提供字符候选提取服务（需要检测引擎）。");
        var region =
            SelectedRegion
            ?? throw new InvalidOperationException("请选择文字ROI，或在图上拖动画出文字行（须为横向单行）。");
        var targets = selectedOnly
            ? _images.SelectedIndex < 0
                ? throw new InvalidOperationException("请选择图像。")
                : new[] { _sources[_images.SelectedIndex] }
            : _sources.ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("请先添加良品图。");
        }

        string? confirmed = string.IsNullOrWhiteSpace(_confirmed.Text) ? null : _confirmed.Text.Trim();
        SetBusy(true);
        try
        {
            int ok = 0;
            var problems = new List<string>();
            foreach (var (image, name) in targets)
            {
                _status.Text = $"正在提取 {name} / {region.Name}…";
                var box = BoxFor(image, region);
                if (!box.Fits(image))
                {
                    problems.Add(name + "：框超出图像，请在该图上重新画框");
                    continue;
                }

                var result = await service.ExtractGlyphCandidatesAsync(image, box, confirmed);
                var segmentation = result.Segmentation;
                if (segmentation.Characters.Count == 0)
                {
                    problems.Add(name + "：" + segmentation.Reason);
                    continue;
                }

                _lines.RemoveAll(l => ReferenceEquals(l.Image, image) && l.Region == region.Name);
                _lines.Add(new LineCandidate(image, name, region.Name, segmentation));
                ok++;
                if (segmentation.Status != "provisional")
                {
                    problems.Add(name + "：切割需复核，默认未勾选（" + segmentation.Reason + "）");
                }
            }

            RefreshSamples();
            ShowImage();
            _status.Text =
                $"已提取{ok}行，共{SampleCount}个样本。"
                + (problems.Count == 0 ? "" : "注意：" + string.Join("；", problems));
        }
        finally
        {
            SetBusy(false);
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

        if (SampleCount == 0)
        {
            throw new InvalidOperationException("没有勾选的字符样本。");
        }

        var few = Counts().Where(c => c.Value < SuggestedSamples).Select(c => c.Key).ToArray();
        if (
            few.Length > 0
            && MessageBox.Show(
                this,
                $"以下字符样本少于{SuggestedSamples}个，阈值可能偏紧（易误报）：{string.Join("", few)}。仍然训练？",
                "样本偏少",
                MessageBoxButtons.OKCancel
            ) != DialogResult.OK
        )
        {
            return;
        }

        var samples = _lines.Select(l => l.ToSample()).ToArray();
        SetBusy(true);
        try
        {
            _status.Text = $"正在训练{Counts().Count}个字符模型…";
            var entries = await Task.Run(() => trainer.TrainCharacters(samples));
            string provenance = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"lines\":{0},\"samples\":{1},\"images\":{2},\"utc\":\"{3:O}\"}}",
                samples.Length,
                SampleCount,
                _lines.Select(l => l.Image).Distinct().Count(),
                DateTimeOffset.UtcNow
            );
            int revision = Manager.PutAnomalyModels(head.Id, head.Revision, entries, true, provenance);
            // 手画的文字行不在配方中，只发布模型，不参与绑定。
            var recipeRegions = _regions
                .Items.Cast<RegionChoice>()
                .Where(r => !r.AdHoc)
                .Select(r => r.Name)
                .ToHashSet(StringComparer.Ordinal);
            LastPublished = (
                head.Id,
                revision,
                _lines.Select(l => l.Region).Distinct().Where(recipeRegions.Contains).ToArray()
            );
            Reload(head.Id);
            _status.Text =
                $"已发布 {head.Name} r{revision}：{entries.Count}个字符模型（{string.Join("", entries.Select(e => e.Key))}）。"
                + "本次未涉及的字符保留原模型；配方需绑定新版本并选择逐字符模式。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Dictionary<string, int> Counts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in _lines)
        {
            for (int i = 0; i < line.Labels.Length; i++)
            {
                if (line.Include[i])
                {
                    counts[line.Labels[i]] = counts.TryGetValue(line.Labels[i], out int n) ? n + 1 : 1;
                }
            }
        }

        return counts;
    }

    private void RefreshSamples()
    {
        _filling = true;
        try
        {
            foreach (DataGridViewRow row in _samples.Rows)
            {
                (row.Cells["Patch"].Value as Image)?.Dispose();
            }

            _samples.Rows.Clear();
            foreach (var line in _lines)
            {
                for (int i = 0; i < line.Characters.Count; i++)
                {
                    int index = _samples.Rows.Add(
                        DrawingImageConverter.ToBitmap(line.Characters[i].Patch),
                        line.Labels[i],
                        line.Include[i],
                        $"{line.Source} / {line.Region} 第{i + 1}位"
                            + (line.Segmentation.Status == "provisional" ? "" : "（切割需复核）")
                    );
                    _samples.Rows[index].Tag = Tuple.Create(line, i);
                }
            }
        }
        finally
        {
            _filling = false;
        }

        RefreshCoverage();
    }

    private void RefreshCoverage()
    {
        _coverage.Items.Clear();
        foreach (var pair in Counts().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var item = new ListViewItem(
                new[]
                {
                    pair.Key,
                    pair.Value.ToString(CultureInfo.InvariantCulture),
                    pair.Value < SuggestedSamples ? "偏少，建议补充" : "",
                }
            );
            if (pair.Value < SuggestedSamples)
            {
                item.ForeColor = Color.FromArgb(198, 40, 40);
            }

            _coverage.Items.Add(item);
        }
    }

    private void CellChanged(int row, int column)
    {
        if (_filling || row < 0 || !(_samples.Rows[row].Tag is Tuple<LineCandidate, int> tag))
        {
            return;
        }

        var cell = _samples.Rows[row].Cells[column];
        if (_samples.Columns[column].Name == "Label")
        {
            string value = (cell.Value as string ?? "").Trim();
            if (value.Length == 1 && FieldSettings.IsAlphanumeric(value[0]))
            {
                tag.Item1.Labels[tag.Item2] = value;
            }
            else
            {
                MessageBox.Show(this, "身份须为单个ASCII字母或数字（区分大小写）。", "身份无效");
                _filling = true;
                cell.Value = tag.Item1.Labels[tag.Item2];
                _filling = false;
            }
        }
        else if (_samples.Columns[column].Name == "Use")
        {
            tag.Item1.Include[tag.Item2] = cell.Value is bool b && b;
        }

        RefreshCoverage();
    }

    private void ShowImage()
    {
        var image = CurrentImage;
        if (image == null)
        {
            return;
        }

        var region = SelectedRegion;
        if (!ReferenceEquals(_shown, image))
        {
            _viewer.SetImage(image);
            _shown = image;
        }

        // 只显示所选文字ROI在本图上的框（编辑序号固定为0）；没有文字ROI时直接拖动即可画出文字行。
        _viewer.EditRegions = region != null;
        _viewer.SetOverlays(
            region == null
                ? Array.Empty<InspectionRegion>()
                : new[]
                {
                    new InspectionRegion(
                        region.Name,
                        ERegionKind.Text,
                        BoxFor(image, region),
                        singleLine: true
                    ),
                },
            Array.Empty<InspectionFinding>()
        );
        _viewer.SetCharacters(
            _lines
                .Where(l => ReferenceEquals(l.Image, image) && (region == null || l.Region == region.Name))
                .SelectMany(l => l.Characters)
        );
        // 只有一个框：直接选中，控制点立即可拖。
        _viewer.SelectedRegionIndex = region == null ? -1 : 0;
    }

    private ImageFrame? _shown;

    private IAnomalyLibraryManager Manager =>
        _manager ?? throw new InvalidOperationException("未连接异常模型库管理器。");

    private AnomalyLibraryInfo? Head => _libraries.SelectedItem as AnomalyLibraryInfo;

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

    private static void Add(Control parent, string text, Func<Task> action)
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
                MessageBox.Show(e.Message, "字符异常模型制作未完成");
            }
        };
        parent.Controls.Add(b);
    }

    private static void Add(Control parent, string text, Action action)
    {
        Add(
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
            foreach (DataGridViewRow row in _samples.Rows)
            {
                (row.Cells["Patch"].Value as Image)?.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
