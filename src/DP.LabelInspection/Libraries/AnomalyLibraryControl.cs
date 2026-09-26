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
/// 异常模型库（质量方法B）的可嵌入管理器，与单字库一致按不可变版本管理：新建/导入/导出/归档，
/// 用已对齐的良品图为选中ROI训练模型并发布新版本。宿主注入管理器与训练实现，控件不依赖具体存储或视觉库。
/// </summary>
public sealed partial class AnomalyLibraryControl : UserControl
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
    private readonly CheckedListBox _regions = new CheckedListBox
    {
        Dock = DockStyle.Left,
        Width = 240,
        CheckOnClick = true,
    };
    private readonly ListView _models = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    private readonly Label _status = new Label
    {
        AutoSize = true,
        Text = "良品图须与当前配方对齐（同一相机/工位）；每个ROI一个模型，默认以ROI名称为键。",
    };
    private readonly Label _goodInfo = new Label { AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
    private readonly List<ImageFrame> _good = new List<ImageFrame>();
    private readonly List<Control> _busyDisabled = new List<Control>();
    private IAnomalyLibraryManager? _manager;
    private IAnomalyModelTrainer? _trainer;

    /// <summary>创建无参数、可安全用于设计器的管理器。</summary>
    public AnomalyLibraryControl()
    {
        Size = new Size(960, 600);
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9);
        _models.Columns.Add("模型键", 150);
        _models.Columns.Add("特征/模式", 150);
        _models.Columns.Add("裁图", 80);
        _models.Columns.Add("良品数", 60);
        _models.Columns.Add("阈值", 70);
        _models.Columns.Add("大小", 70);
        _models.Columns.Add("标定", 400);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange(new Control[] { _libraries, _revision });
        Add(top, "刷新", () => Reload(Head?.Id));
        Add(top, "读取版本", ShowRevision);
        Add(
            top,
            "新建模型库",
            () =>
            {
                string? name = EditorDialogs.Ask("模型库名称（产品/工位等）", "");
                if (name != null)
                {
                    Reload(Manager.CreateAnomalyLibrary(name));
                }
            }
        );
        Add(
            top,
            "导入",
            () =>
            {
                using var d = new OpenFileDialog { Filter = "异常模型库|*.json" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    Reload(Manager.ImportAnomalyLibrary(File.ReadAllText(d.FileName)));
                }
            }
        );
        Add(
            top,
            "导出",
            () =>
            {
                var head = Selected;
                using var d = new SaveFileDialog
                {
                    Filter = "异常模型库|*.json",
                    FileName = head.Id + "-r" + _revision.Value + ".json",
                };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(
                        d.FileName,
                        Manager.ExportAnomalyLibrary(head.Id, (int)_revision.Value)
                    );
                }
            }
        );
        Add(
            top,
            "归档/恢复",
            () =>
            {
                var head = Selected;
                Manager.ArchiveAnomalyLibrary(head.Id, head.Revision, !head.Archived);
                Reload(head.Id);
            }
        );

        var train = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        Add(
            train,
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
                    _good.Add(DrawingImageConverter.FromImage(image));
                }

                ShowGood();
            }
        );
        Add(
            train,
            "清空良品图",
            () =>
            {
                _good.Clear();
                ShowGood();
            }
        );
        train.Controls.Add(_goodInfo);
        Add(train, "训练选中ROI并发布新版本", async () => await TrainAsync());
        Add(
            train,
            "删除所选模型",
            () =>
            {
                var head = Selected;
                var keys = _models.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToArray();
                if (keys.Length == 0)
                {
                    throw new InvalidOperationException("请选择要删除的模型。");
                }

                int revision = head.Revision;
                foreach (string key in keys)
                {
                    revision = Manager.RemoveAnomalyModel(head.Id, revision, key);
                }

                Reload(head.Id);
                _status.Text = $"已发布 r{revision}：删除 {keys.Length} 个模型；已绑定旧版本的配方不受影响。";
            }
        );
        _busyDisabled.AddRange(new Control[] { top, train, _regions });

        var middle = new Panel { Dock = DockStyle.Fill };
        middle.Controls.Add(_models);
        middle.Controls.Add(_regions);
        var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        status.Controls.Add(_status);
        Controls.Add(middle);
        Controls.Add(top);
        Controls.Add(train);
        Controls.Add(status);
        _libraries.SelectedIndexChanged += (_, _) =>
        {
            if (Head != null)
            {
                _revision.Value = Math.Min(_revision.Maximum, Head.Revision);
                ShowRevision();
            }
        };
        ShowGood();
    }

    /// <summary>最近一次训练发布的模型库、版本及训练的ROI名称；宿主可据此把ROI绑定到新版本。</summary>
    public (string LibraryId, int Revision, IReadOnlyList<string> Regions)? LastPublished
    {
        get;
        private set;
    }

    /// <summary>连接宿主拥有的模型库管理器及可选训练实现（无训练实现时只能管理和导入导出）。</summary>
    /// <param name = "manager">宿主拥有的异常模型库管理器，控件不负责释放。</param>
    /// <param name = "trainer">宿主拥有的训练实现。</param>
    public void AttachManager(IAnomalyLibraryManager manager, IAnomalyModelTrainer? trainer = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _trainer = trainer;
        Reload();
    }

    /// <summary>设置可训练的ROI（配方坐标），忽略区不列出；已绑定模型库的ROI默认勾选。</summary>
    /// <param name = "regions">当前配方的ROI。</param>
    public void SetRegions(IEnumerable<InspectionRegion> regions)
    {
        _regions.Items.Clear();
        foreach (
            var r in (regions ?? throw new ArgumentNullException(nameof(regions))).Where(r =>
                r.Kind != ERegionKind.Ignore
            )
        )
        {
            _regions.Items.Add(new RegionItem(r), r.Tasks.DetectAnomaly || r.Anomaly != null);
        }
    }

    /// <summary>加入一张已与配方对齐的良品整图（例如当前载入的待检图）。</summary>
    /// <param name = "image">独立不可变整图。</param>
    public void AddGoodImage(ImageFrame image)
    {
        _good.Add(image ?? throw new ArgumentNullException(nameof(image)));
        ShowGood();
    }

    private IAnomalyLibraryManager Manager =>
        _manager ?? throw new InvalidOperationException("未连接异常模型库管理器。");

    private AnomalyLibraryInfo? Head => _libraries.SelectedItem as AnomalyLibraryInfo;

    private AnomalyLibraryInfo Selected => Head ?? throw new InvalidOperationException("请选择模型库。");

    private void ShowGood()
    {
        _goodInfo.Text =
            _good.Count == 0
                ? "尚未添加良品图"
                : $"良品图 {_good.Count} 张（{_good[0].Width}×{_good[0].Height}）";
    }

    private async Task TrainAsync()
    {
        var head = Selected;
        var trainer = _trainer ?? throw new InvalidOperationException("宿主未提供训练实现。");
        var regions = _regions.CheckedItems.Cast<RegionItem>().Select(i => i.Region).ToArray();
        if (regions.Length == 0)
        {
            throw new InvalidOperationException("请勾选要训练的ROI。");
        }

        if (_good.Count == 0)
        {
            throw new InvalidOperationException("请先添加良品图。");
        }

        if (_good.Any(g => g.Width != _good[0].Width || g.Height != _good[0].Height))
        {
            throw new InvalidOperationException("良品图尺寸不一致；须来自同一工位且已与配方对齐。");
        }

        if (head.Archived)
        {
            throw new InvalidOperationException("已归档的模型库不能添加模型，请先恢复。");
        }

        var existing = Manager.LoadAnomalyLibrary(head.Id, head.Revision).Models;
        var replaced = regions.Where(r => existing.ContainsKey(r.Name)).Select(r => r.Name).ToArray();
        if (
            replaced.Length > 0
            && MessageBox.Show(
                this,
                "以下ROI已有模型，将在新版本中替换（旧版本保留）：" + string.Join("、", replaced),
                "替换模型",
                MessageBoxButtons.OKCancel
            ) != DialogResult.OK
        )
        {
            return;
        }

        var good = _good.ToArray();
        SetBusy(true);
        try
        {
            var entries = new List<AnomalyModelEntry>();
            foreach (var region in regions)
            {
                _status.Text = $"正在训练 {region.Name}（{good.Length}张良品）…";
                entries.Add(await Task.Run(() => trainer.Train(good, region)));
            }

            int revision = head.Revision;
            string provenance = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"training_images\":{0},\"image_size\":\"{1}x{2}\",\"utc\":\"{3:O}\"}}",
                good.Length,
                good[0].Width,
                good[0].Height,
                DateTimeOffset.UtcNow
            );
            foreach (var entry in entries)
            {
                revision = Manager.PutAnomalyModel(head.Id, revision, entry, true, provenance);
            }

            LastPublished = (head.Id, revision, regions.Select(r => r.Name).ToArray());
            Reload(head.Id);
            _status.Text =
                $"已发布 {head.Name} r{revision}："
                + string.Join(
                    "；",
                    entries.Select(e =>
                        $"{e.Key} 阈值{e.Threshold:F3}{(e.LocalRadius > 0 ? "（位置相关）" : "")}"
                    )
                )
                + "。配方需绑定新版本才会使用。";
        }
        finally
        {
            SetBusy(false);
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

    private void Reload(string? id = null)
    {
        if (_manager == null)
        {
            return;
        }

        var items = Manager.ListAnomalyLibraries(true);
        _libraries.Items.Clear();
        foreach (var item in items)
        {
            _libraries.Items.Add(item);
        }

        if (items.Count > 0)
        {
            _libraries.SelectedItem = items.FirstOrDefault(i => i.Id == id) ?? items[0];
        }
        else
        {
            _models.Items.Clear();
        }
    }

    private void ShowRevision()
    {
        if (_manager == null || Head == null)
        {
            return;
        }

        _models.Items.Clear();
        var snapshot = Manager.LoadAnomalyLibrary(Head.Id, (int)_revision.Value);
        foreach (var m in snapshot.Models.Values.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            _models.Items.Add(
                new ListViewItem(
                    new[]
                    {
                        m.Key,
                        (m.FeatureSource.StartsWith("cnn", StringComparison.Ordinal) ? "CNN" : "手工")
                            + (m.LocalRadius > 0 ? $" / 位置相关±{m.LocalRadius}" : " / 与位置无关"),
                        m.Width + "×" + m.Height,
                        m.TrainingImages.ToString(CultureInfo.InvariantCulture),
                        m.Threshold.ToString("F3", CultureInfo.InvariantCulture),
                        (m.Length / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB",
                        m.Calibration,
                    }
                )
            );
        }

        _status.Text = $"{snapshot.Name} r{snapshot.Revision}：{snapshot.Models.Count} 个模型。";
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
                MessageBox.Show(e.Message, "异常模型库操作未完成");
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
}
