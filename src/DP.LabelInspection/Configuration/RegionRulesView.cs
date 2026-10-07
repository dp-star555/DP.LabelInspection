using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

/// <summary>
/// ROI列表＋所选ROI的检测项目与规则属性。对话框（保存后生效）与可嵌入的<see cref="RegionRulesControl"/>（即时生效）共用。
/// </summary>
internal sealed class RegionRulesView : UserControl
{
    private readonly ListBox _list = new ListBox { Dock = DockStyle.Top, Height = 150, IntegralHeight = false };
    private readonly PropertyGrid _grid = new PropertyGrid
    {
        Dock = DockStyle.Fill,
        HelpVisible = false,
        ToolbarVisible = false,
        PropertySort = PropertySort.CategorizedAlphabetical,
    };
    private readonly TextBox _help = new TextBox
    {
        Name = "RoiParameterHelp",
        Dock = DockStyle.Bottom,
        Height = 96,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly ComboBox _libraries = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _anomalyLibraries = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly List<RegionEditor.EditableRegion> _values = new List<RegionEditor.EditableRegion>();
    private readonly string _idleHelp;
    private IAnomalyLibraryManager? _anomalyManager;

    /// <summary>内容被用户修改（属性、绑定库、删除）后触发；即时模式的宿主据此写回。</summary>
    internal event EventHandler? Edited;

    /// <param name = "idleHelp">未选中参数时的说明。</param>
    internal RegionRulesView(string idleHelp)
    {
        _idleHelp = idleHelp;
        _help.Text = idleHelp;
        var bindings = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 2, 0, 2),
        };
        var bind = ActionGroup.CreateButton("绑定所选字库/版本", BindLibrary);
        var bindAnomaly = ActionGroup.CreateButton("绑定所选异常模型库/版本", BindAnomalyLibrary);
        var remove = ActionGroup.CreateButton("删除选中ROI", RemoveSelected);
        bindings.Controls.AddRange(new Control[] { _libraries, bind, _anomalyLibraries, bindAnomaly, remove });

        _grid.SelectedGridItemChanged += (_, _) => Describe();
        _grid.PropertyValueChanged += (_, _) =>
        {
            Describe();
            RefreshListText();
            Edited?.Invoke(this, EventArgs.Empty);
        };
        _list.SelectedIndexChanged += (_, _) => _grid.SelectedObject = _list.SelectedItem;
        // 停靠按Z序倒序处理：列表在上，说明与绑定在下，属性占余下空间。
        Controls.Add(_grid);
        Controls.Add(_list);
        Controls.Add(_help);
        Controls.Add(bindings);
    }

    /// <summary>当前是否有ROI。</summary>
    internal int Count => _values.Count;

    /// <summary>显示说明或错误（错误以醒目颜色显示）。</summary>
    internal void ShowMessage(string text, bool error = false)
    {
        _help.Text = text;
        _help.ForeColor = error ? Color.Firebrick : ForeColor;
    }

    /// <summary>重新载入ROI与可绑定的库列表，尽量保持原选中ROI。</summary>
    internal void SetRegions(
        IEnumerable<InspectionRegion> regions,
        IGlyphLibraryManager? manager,
        IAnomalyLibraryManager? anomalyManager
    )
    {
        string? selected = (_list.SelectedItem as RegionEditor.EditableRegion)?.Name;
        _anomalyManager = anomalyManager;
        _values.Clear();
        _values.AddRange(regions.Select(r => new RegionEditor.EditableRegion(r)));
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var value in _values)
        {
            _list.Items.Add(value);
        }

        _list.EndUpdate();
        int index = _values.FindIndex(v => v.Name == selected);
        _list.SelectedIndex = index >= 0 ? index : _values.Count > 0 ? 0 : -1;
        if (_list.SelectedIndex < 0)
        {
            _grid.SelectedObject = null;
        }

        Fill(_libraries, manager?.ListLibraries().Cast<object>());
        Fill(_anomalyLibraries, anomalyManager?.ListAnomalyLibraries().Cast<object>());
    }

    /// <summary>按名称选中ROI（例如画布上选中后同步）。</summary>
    internal void SelectRegion(string name)
    {
        int index = _values.FindIndex(v => v.Name == name);
        if (index >= 0)
        {
            _list.SelectedIndex = index;
        }
    }

    /// <summary>生成不可变ROI配置；名称必须唯一。</summary>
    internal InspectionRegion[] Build()
    {
        var result = _values.Select(v => v.Build()).ToArray();
        if (result.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new ArgumentException("ROI名称必须唯一。");
        }

        return result;
    }

    private static void Fill(ComboBox box, IEnumerable<object>? items)
    {
        object? old = box.SelectedItem;
        box.Items.Clear();
        foreach (var item in items ?? Enumerable.Empty<object>())
        {
            box.Items.Add(item);
        }

        if (old != null && box.Items.Contains(old))
        {
            box.SelectedItem = old;
        }
    }

    private void Describe()
    {
        var property = _grid.SelectedGridItem?.PropertyDescriptor;
        ShowMessage(property == null ? _idleHelp : property.DisplayName + "\r\n" + property.Description);
    }

    private void RefreshListText()
    {
        // ListBox只在项目替换时重绘文字；名称/类型改变后刷新所选项显示。
        int index = _list.SelectedIndex;
        if (index >= 0)
        {
            _list.Items[index] = _list.Items[index];
        }
    }

    private void BindLibrary()
    {
        if (_list.SelectedItem is RegionEditor.EditableRegion r && _libraries.SelectedItem is GlyphLibraryInfo l)
        {
            r.LibraryId = l.Id;
            r.LibraryRevision = l.Revision;
            _grid.Refresh();
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItem is RegionEditor.EditableRegion r)
        {
            _values.Remove(r);
            _list.Items.Remove(r);
            _grid.SelectedObject = _list.SelectedItem;
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BindAnomalyLibrary()
    {
        if (
            !(_list.SelectedItem is RegionEditor.EditableRegion r)
            || !(_anomalyLibraries.SelectedItem is AnomalyLibraryInfo l)
            || _anomalyManager == null
        )
        {
            return;
        }

        r.AnomalyLibraryId = l.Id;
        r.AnomalyLibraryRevision = l.Revision;
        // 按库中实际内容选择模式：有本ROI的整ROI模型用整ROI；文字ROI且库中是字符模型则选逐字符；都没有时立即提示。
        var models = _anomalyManager.LoadAnomalyLibrary(l.Id, l.Revision).Models;
        string key = string.IsNullOrWhiteSpace(r.AnomalyModelKey) ? r.Name : r.AnomalyModelKey!;
        bool whole = models.TryGetValue(key, out var own) && own.Scope == EAnomalyModelScope.Region;
        // 字符模型按字符组（null为不分组）归类；优先与模型键/ROI同名的组，其次不分组，再次唯一的组。
        var groups = models
            .Values.Where(m => m.Scope == EAnomalyModelScope.Character)
            .GroupBy(m => m.Group ?? "")
            .ToDictionary(
                g => g.Key,
                g => string.Concat(g.Select(m => m.Character).OrderBy(c => c, StringComparer.Ordinal))
            );
        string? group =
            groups.ContainsKey(key) ? key
            : groups.ContainsKey("") ? ""
            : groups.Count == 1 ? groups.Keys.Single()
            : null;
        string Describe(string g) => (g.Length == 0 ? "不分组" : "组[" + g + "]") + "：" + groups[g];
        string note;
        if (whole)
        {
            r.AnomalyPerCharacter = false;
            note = $"已绑定 {l.Name} r{l.Revision}：整ROI模型[{key}]。";
        }
        else if (r.Kind == ERegionKind.Text && group != null)
        {
            r.AnomalyPerCharacter = true;
            r.AnomalyModelKey = group.Length == 0 ? null : group;
            note =
                $"已绑定 {l.Name} r{l.Revision}：字符模型（{Describe(group)}），已自动选择“逐字符检查”（需要OCR）。"
                + (groups.Count > 1 ? "库中还有其他字符组，可在“模型键”中改填。" : "");
        }
        else
        {
            var regions = models
                .Values.Where(m => m.Scope == EAnomalyModelScope.Region)
                .Select(m => m.Key)
                .ToArray();
            note =
                $"注意：{l.Name} r{l.Revision} 中没有模型[{key}]，检测时本ROI的B会判NG。"
                + (
                    regions.Length == 0
                        ? "库中没有整ROI模型。"
                        : "库中现有整ROI模型：" + string.Join("、", regions) + "（可在“模型键”中填写）。"
                )
                + (
                    groups.Count == 0 ? ""
                    : r.Kind == ERegionKind.Text
                        ? "库中有多个字符组（"
                            + string.Join("；", groups.Keys.OrderBy(g => g, StringComparer.Ordinal).Select(Describe))
                            + "），请选“逐字符检查”并在“模型键”中填写字符组。"
                    : "字符模型只能用于文字ROI的逐字符检查。"
                )
                + "请先在“批量训练(B)”中为本ROI训练并发布。";
            MessageBox.Show(FindForm(), note, "异常模型库中没有此ROI的模型");
        }

        ShowMessage(note);
        _grid.Refresh();
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
