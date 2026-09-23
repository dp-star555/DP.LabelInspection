# 条码：汇总F项＋完整子缺陷

## WinForms呈现

- 一个条码/QR区域显示一个编号，例如`F1 NG`，主画布不再铺满小缺陷编号。
- 主证据列表同样只显示条码汇总行。汇总按NG > REVIEW > OK聚合最终、已过质量门禁的子项，读码OK不能覆盖打印NG。
- 点击列表或主画布F项，在“缺陷标记 / 单字”页只显示缺陷标记图＋子项明细，已移除重复原图面板。**不是任意重编码得到的良品模板。**
- 小缺陷只在对比区标框，不铺编号；下方明细保留`F1.1`、`F1.2`等、状态、检查代码、原图坐标、原始像素面积和说明。读码/绑定/不确定性信息也保留。
- 标记视图支持滚轮缩放与中/右键平移；双击明细可定位放大。“独立放大查看”可打开大窗口，不受底部页高度限制。
- 原有文字/单字证据选择不变。此轮主界面增强针对WinForms，未声称WPF编辑/对比交互完全一致。

## SDK与JSON/ZIP

新增`InspectionReport.EvidenceGroups`，自动从最终报告生成稳定的本报告内F编号：

```csharp
foreach (var group in report.EvidenceGroups)
{
    // group.Id = "F1"; group.Status = "NG"; group.RegionName = "ROI-1"
    // group.Summary 包含汇总范围和结论
    foreach (var child in group.Children)
    {
        // child.Id = "F1.2"; child.Status = "NG"
        var evidence = child.Finding;
        // evidence.Code / Verdict / Bounds / AreaPixels / Message
    }
}
```

`report.json`包含相同的`evidenceGroups → children → finding`结构。只取NG子项时按`child.Status=="NG"`筛选，不能靠说明文字判断。

为兼容现有调用方，**原`Analysis.Regions[].Findings`与全局`Findings`完整保留**，没有删除、吞掉或用汇总替换小缺陷。新字段是明确的分组视图，不改变算法、坐标和面积。父级不冒充小缺陷面积的像素并集。

导出总览图也改为条码汇总框与F编号；原图和全部明细继续随报告保存及哈希校验ZIP导出。旧报告文件不原地改写，重新检测会生成新的分组报告。

F编号只在当前报告内稳定，不是跨图片的追踪ID。默认分组识别解码观察或`barcode_`/`qr_`诊断代码；自定义后端应遵守该证据约定。

## 验证

- 汇总NG/REVIEW、编号与全部子项原始对象保留。
- JSON回读、原字段兼容、哈希校验ZIP中的嵌套明细。
- 导出主图不再画每个小缺陷。
- 实际CODE128断条、双运行时WinForms真实选择、主列表/画布仅汇总、单一标记视图无重复原图、子项数量、缩放平移及查看不修改报告。另验证选择“二维码（QR）”绘制后保留类型声明。
- 明确码区读码执行失败为NG；局部网格不可用仍可在子项中说明REVIEW，不能因此把父NG降成REVIEW。

分组不会提高或降低检测精度，只解决展示拥挤与输出层次问题。
