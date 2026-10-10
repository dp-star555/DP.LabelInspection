using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;
using ModernUI.WinForms;

namespace DP.LabelInspection;

/// <summary>
/// ROI规则卡片列表：每张卡片的标题为ROI名称，单击展开后以分组属性表显示该ROI的检测项目与规则（字库、异常模型库为下拉参数）。
/// 对话框（保存后生效）与可嵌入的<see cref="RegionRulesControl"/>（即时生效）共用。
/// </summary>
internal sealed class RegionRulesView : UserControl
{
    private static readonly ModernTheme Theme = ModernTheme.Dark;
    private readonly ModernScrollView _scroll = new ModernScrollView { Dock = DockStyle.Fill };
    private readonly CardStack _stack;
    private readonly Label _help = new Label
    {
        Name = "RoiParameterHelp",
        Dock = DockStyle.Bottom,
        AutoSize = false,
        Height = 54,
        Padding = new Padding(8, 6, 8, 6),
        AutoEllipsis = true,
    };
    private readonly List<RegionCard> _cards = new List<RegionCard>();
    private readonly string _idleHelp;
    private LibrarySources _sources = LibrarySources.Empty;
    private IAnomalyLibraryManager? _anomalyManager;
    private bool _anomalyWarning;

    /// <summary>内容被用户修改（属性、库绑定、删除）后触发；即时模式的宿主据此写回。</summary>
    internal event EventHandler? Edited;

    /// <summary>用户展开某张ROI卡片后触发（参数为ROI名称），宿主可在画布上选中同一ROI。</summary>
    internal event EventHandler<string>? RegionActivated;

    /// <param name = "idleHelp">没有提示时显示的说明。</param>
    internal RegionRulesView(string idleHelp)
    {
        _idleHelp = idleHelp;
        _stack = new CardStack(this);
        _scroll.Content = _stack;
        _scroll.ApplyTheme(Theme);
        _scroll.SizeChanged += (_, _) => _stack.PerformLayout();
        BackColor = _stack.BackColor = Theme.Background;
        _help.BackColor = Theme.Background;
        ShowMessage(idleHelp);
        Controls.Add(_scroll);
        Controls.Add(_help);
    }

    /// <summary>当前ROI数量。</summary>
    internal int Count => _cards.Count;

    /// <summary>显示说明或错误（错误以醒目颜色显示）。</summary>
    internal void ShowMessage(string text, bool error = false)
    {
        _help.Text = text;
        _help.ForeColor = error ? Theme.Error : Theme.TextSecondary;
    }

    /// <summary>重新载入ROI与可绑定的库列表，保持原展开的ROI及滚动位置。</summary>
    internal void SetRegions(
        IEnumerable<InspectionRegion> regions,
        IGlyphLibraryManager? manager,
        IAnomalyLibraryManager? anomalyManager
    )
    {
        string? expanded = Expanded?.Value.Name;
        int offset = _scroll.ScrollOffset;
        _anomalyManager = anomalyManager;
        _sources = new LibrarySources(manager, anomalyManager);
        _stack.SuspendLayout();
        try
        {
            foreach (var card in _cards)
            {
                card.Dispose();
            }

            _cards.Clear();
            foreach (var region in regions)
            {
                var card = new RegionCard(this, new RegionEditor.EditableRegion(region) { Sources = _sources });
                _cards.Add(card);
                _stack.Controls.Add(card);
            }

            Expand(_cards.FirstOrDefault(c => c.Value.Name == expanded), false);
        }
        finally
        {
            _stack.ResumeLayout(true);
        }

        _scroll.ScrollOffset = offset;
    }

    /// <summary>按名称展开ROI卡片（例如画布上选中后同步），并滚动到该卡片。</summary>
    internal void SelectRegion(string name)
    {
        var card = _cards.FirstOrDefault(c => c.Value.Name == name);
        if (card != null && card != Expanded)
        {
            Expand(card, false);
            _scroll.ScrollOffset = card.Top;
        }
    }

    /// <summary>生成不可变ROI配置；名称必须唯一。</summary>
    internal InspectionRegion[] Build()
    {
        foreach (var card in _cards)
        {
            card.CommitPendingEdit();
        }

        var result = _cards.Select(c => c.Value.Build()).ToArray();
        if (result.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new ArgumentException("ROI名称必须唯一。");
        }

        return result;
    }

    private RegionCard? Expanded => _cards.FirstOrDefault(c => c.IsExpanded);

    private void Expand(RegionCard? card, bool user)
    {
        foreach (var other in _cards)
        {
            other.IsExpanded = other == card;
        }

        _stack.PerformLayout();
        if (user && card != null)
        {
            RegionActivated?.Invoke(this, card.Value.Name);
        }
    }

    private void Toggle(RegionCard card) => Expand(card.IsExpanded ? null : card, true);

    private void Remove(RegionCard card)
    {
        _cards.Remove(card);
        card.Dispose();
        _stack.PerformLayout();
        ShowMessage("已删除ROI“" + card.Value.Name + "”。");
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged(RegionCard card, string property)
    {
        var value = card.Value;
        if (property == nameof(RegionEditor.EditableRegion.AnomalyLibrary))
        {
            string note = DescribeAnomalyBinding(value);
            ShowMessage(note, _anomalyWarning);
            card.RefreshProperties(
                nameof(RegionEditor.EditableRegion.AnomalyPerCharacter),
                nameof(RegionEditor.EditableRegion.AnomalyModelKey)
            );
        }
        else if (property == nameof(RegionEditor.EditableRegion.GlyphLibrary))
        {
            ShowMessage(
                value.LibraryId == null
                    ? value.Name + "：已取消字库绑定，不执行单字外观比较。"
                    : value.Name + "：已绑定字库 " + value.GlyphLibrary + "。"
            );
        }
        else
        {
            ShowMessage(_idleHelp);
        }

        card.Invalidate(true);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    // 选择异常模型库后按库中实际内容选择模式：有本ROI的整ROI模型用整ROI；文字ROI且库中是字符模型则选逐字符；都没有时给出提示。
    private string DescribeAnomalyBinding(RegionEditor.EditableRegion r)
    {
        _anomalyWarning = false;
        if (r.AnomalyLibraryId == null || r.AnomalyLibraryRevision == null)
        {
            return r.Name + "：已取消异常模型库绑定。";
        }

        var choice = r.AnomalyLibrary;
        if (_anomalyManager == null)
        {
            return r.Name + "：已绑定 " + choice + "。";
        }

        var models = _anomalyManager.LoadAnomalyLibrary(r.AnomalyLibraryId, r.AnomalyLibraryRevision.Value).Models;
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
        if (whole)
        {
            r.AnomalyPerCharacter = false;
            return $"{r.Name}：已绑定 {choice}，整ROI模型[{key}]。";
        }

        if (r.Kind == ERegionKind.Text && group != null)
        {
            r.AnomalyPerCharacter = true;
            r.AnomalyModelKey = group.Length == 0 ? null : group;
            return $"{r.Name}：已绑定 {choice}，字符模型（{Describe(group)}），已自动选择“逐字符检查”（需要OCR）。"
                + (groups.Count > 1 ? "库中还有其他字符组，可在“模型键”中改填。" : "");
        }

        _anomalyWarning = true;
        var regions = models.Values.Where(m => m.Scope == EAnomalyModelScope.Region).Select(m => m.Key).ToArray();
        return $"注意：{choice} 中没有模型[{key}]，检测时{r.Name}的B会判NG。"
            + (regions.Length == 0 ? "库中没有整ROI模型。" : "库中现有整ROI模型：" + string.Join("、", regions) + "（可在“模型键”中填写）。")
            + (
                groups.Count == 0 ? ""
                : r.Kind == ERegionKind.Text
                    ? "库中有多个字符组（"
                        + string.Join("；", groups.Keys.OrderBy(g => g, StringComparer.Ordinal).Select(Describe))
                        + "），请选“逐字符检查”并在“模型键”中填写字符组。"
                : "字符模型只能用于文字ROI的逐字符检查。"
            )
            + "请先在异常模型窗口中为本ROI训练并发布。";
    }

    /// <summary>纵向排列卡片；展开的卡片占满剩余高度（至少可显示若干行参数），超出时由外层滚动。</summary>
    private sealed class CardStack : Panel
    {
        private readonly RegionRulesView _owner;

        internal CardStack(RegionRulesView owner)
        {
            _owner = owner;
            DoubleBuffered = true;
        }

        private int Gap => LogicalToDeviceUnits(6);

        private int ExpandedBody(int viewport)
        {
            int headers = _owner._cards.Count * (RegionCard.HeaderHeight(this) + Gap);
            return Math.Max(LogicalToDeviceUnits(380), viewport - headers - Gap);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int viewport = _owner._scroll.ClientSize.Height;
            int height = Gap;
            foreach (var card in _owner._cards)
            {
                height += RegionCard.HeaderHeight(this) + Gap + (card.IsExpanded ? ExpandedBody(viewport) : 0);
            }

            return new Size(proposedSize.Width, height);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int gap = Gap,
                y = gap,
                width = Math.Max(0, ClientSize.Width - 2 * gap),
                body = ExpandedBody(_owner._scroll.ClientSize.Height);
            foreach (var card in _owner._cards)
            {
                int height = RegionCard.HeaderHeight(this) + (card.IsExpanded ? body : 0);
                card.SetBounds(gap, y, width, height);
                y += height + gap;
            }
        }
    }

    /// <summary>一张ROI卡片：可点击的标题（名称、类型、尺寸、删除）＋展开后的属性表。</summary>
    private sealed class RegionCard : Panel
    {
        private readonly RegionRulesView _owner;
        private readonly CardHeader _header;
        private ModernPropertyGrid.WinForms.ModernPropertyGrid? _grid;
        private bool _expanded;

        internal RegionCard(RegionRulesView owner, RegionEditor.EditableRegion value)
        {
            _owner = owner;
            Value = value;
            BackColor = Theme.Container;
            Padding = new Padding(1);
            _header = new CardHeader(this) { Dock = DockStyle.Top };
            Controls.Add(_header);
        }

        internal RegionEditor.EditableRegion Value { get; }

        internal static int HeaderHeight(Control control) => control.LogicalToDeviceUnits(40);

        internal bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value)
                {
                    return;
                }

                _expanded = value;
                if (value && _grid == null)
                {
                    // 属性表只在首次展开时创建，ROI较多时不为折叠卡片构建编辑器。
                    _grid = new ModernPropertyGrid.WinForms.ModernPropertyGrid
                    {
                        Dock = DockStyle.Fill,
                        Theme = Theme,
                        ShowSearchBar = false,
                        AnimateCategoryExpansion = false,
                    };
                    _grid.RegisterEditor(LibraryChoiceEditorProvider.Instance);
                    _grid.SelectedObject = Value;
                    _grid.PropertyValueChanged += (_, e) => _owner.OnPropertyChanged(this, e.Property.Name);
                    Controls.Add(_grid);
                    _grid.BringToFront();
                }

                if (_grid != null)
                {
                    _grid.Visible = value;
                }

                _header.Invalidate();
                Invalidate();
            }
        }

        internal void CommitPendingEdit() => _grid?.CommitPendingEdit();

        internal void RefreshProperties(params string[] names)
        {
            foreach (var name in names)
            {
                _grid?.RefreshProperty(name);
            }
        }

        internal void Toggle() => _owner.Toggle(this);

        internal void Remove() => _owner.Remove(this);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(_expanded ? Theme.Primary : Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Invalidate();
        }
    }

    /// <summary>卡片标题：展开箭头、ROI名称、类型与尺寸，右侧删除按钮。</summary>
    private sealed class CardHeader : Control
    {
        private readonly RegionCard _card;
        private readonly ModernButton _remove = new ModernButton
        {
            ButtonType = ModernButtonType.Text,
            Icon = ModernIconKind.Close,
            Theme = Theme,
            Dock = DockStyle.Right,
            AccessibleName = "删除此ROI",
        };
        private bool _hover;

        internal CardHeader(RegionCard card)
        {
            _card = card;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint
                    | ControlStyles.ResizeRedraw,
                true
            );
            Cursor = Cursors.Hand;
            Height = RegionCard.HeaderHeight(this);
            _remove.Width = LogicalToDeviceUnits(36);
            _remove.Click += (_, _) => _card.Remove();
            Controls.Add(_remove);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _card.Toggle();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_card.IsExpanded ? Theme.Elevated : _hover ? Theme.ControlHover : Theme.Container);
            int S(int logical) => LogicalToDeviceUnits(logical);
            var value = _card.Value;
            using (
                var chevron = ModernIcons.CreateBitmap(
                    _card.IsExpanded ? ModernIconKind.ChevronDown : ModernIconKind.ChevronRight,
                    Theme.TextSecondary,
                    S(14)
                )
            )
            {
                g.DrawImage(chevron, S(10), (Height - chevron.Height) / 2);
            }
            int right = Width - _remove.Width - S(8);
            string kind = new ChineseRegionKindConverter().ConvertToString(value.Kind) ?? value.Kind.ToString();
            string summary = kind + " · " + value.Width + "×" + value.Height
                + (value.LibraryId != null ? " · 字库" : "")
                + (value.AnomalyLibraryId != null ? " · 异常模型" : "");
            const TextFormatFlags Line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            var summarySize = TextRenderer.MeasureText(g, summary, Font, Size.Empty, TextFormatFlags.SingleLine);
            int summaryLeft = Math.Max(S(140), right - summarySize.Width);
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                TextRenderer.DrawText(
                    g,
                    value.Name,
                    bold,
                    new Rectangle(S(32), 0, Math.Max(0, summaryLeft - S(40)), Height),
                    Theme.Text,
                    Line
                );
            }

            TextRenderer.DrawText(
                g,
                summary,
                Font,
                new Rectangle(summaryLeft, 0, Math.Max(0, right - summaryLeft), Height),
                Theme.TextSecondary,
                Line | TextFormatFlags.Right
            );
        }
    }
}
