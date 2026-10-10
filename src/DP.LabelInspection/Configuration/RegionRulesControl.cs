using System;
using System.Drawing;
using System.Windows.Forms;

namespace DP.LabelInspection;

/// <summary>
/// 可嵌入宿主页面的ROI规则编辑：每个ROI一张卡片，单击标题展开该ROI的类型、检测项目与规则（字库、异常模型库为下拉参数），
/// 修改即时写回所连接的工作台。字段绑定等操作由宿主另行提供入口（例如工作台的<see cref="LabelInspectionControl.EditFieldBindings"/>）。
/// 与工作台的“编辑ROI/规则”窗口内容一致；画布上绘制、调整或删除ROI后自动刷新，
/// 画布选中的ROI自动展开，展开的卡片也在画布上选中。
/// </summary>
public sealed class RegionRulesControl : UserControl
{
    private readonly LabelInspectionControl _workbench;
    private readonly RegionRulesView _view = new RegionRulesView("") { Dock = DockStyle.Fill };
    private bool _writing;

    /// <summary>连接工作台；控件只通过工作台的公开方法读写ROI。</summary>
    /// <param name = "workbench">宿主拥有的标签工作台。</param>
    public RegionRulesControl(LabelInspectionControl workbench)
    {
        _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
        Font = new Font("Microsoft YaHei UI", 9);
        Controls.Add(_view);
        _view.Edited += (_, _) => WriteBack();
        _view.RegionActivated += (_, name) => _workbench.SelectRegion(name);
        workbench.RegionsChanged += OnRegionsChanged;
        workbench.BusyChanged += OnBusyChanged;
        workbench.SelectedRegionChanged += OnSelectedRegionChanged;
        Reload();
    }

    /// <summary>重新读取工作台ROI及可绑定的字库/异常模型库（例如宿主刚连接了库管理器）。</summary>
    public void Reload()
    {
        _view.SetRegions(_workbench.Regions, _workbench.LibraryManager, _workbench.AnomalyLibraryManager);
        _view.ShowMessage(_view.Count == 0 ? "还没有ROI：在画布上按所选类型左键拖动新建。" : "");
    }

    private void WriteBack()
    {
        try
        {
            var regions = _view.Build();
            _writing = true;
            try
            {
                _workbench.SetRegions(regions);
            }
            finally
            {
                _writing = false;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // 无效组合（例如名称重复、库ID缺版本）不写回，保留编辑内容等待更正。
            _view.ShowMessage("未生效：" + error.Message, error: true);
        }
    }

    private void OnRegionsChanged(object? sender, EventArgs e)
    {
        if (!_writing)
        {
            Reload();
        }
    }

    private void OnBusyChanged(object? sender, EventArgs e)
    {
        Enabled = !_workbench.IsInspectionRunning;
    }

    private void OnSelectedRegionChanged(object? sender, EventArgs e)
    {
        if (_workbench.SelectedRegionName is { } name)
        {
            _view.SelectRegion(name);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _workbench.RegionsChanged -= OnRegionsChanged;
            _workbench.BusyChanged -= OnBusyChanged;
            _workbench.SelectedRegionChanged -= OnSelectedRegionChanged;
        }

        base.Dispose(disposing);
    }
}
