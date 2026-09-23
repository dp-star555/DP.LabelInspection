# 必检单字外观与ROI汇总策略

## 用户规则

1. Text ROI显式绑定字库修订，或显式要求等分单字外观检查后，比对未完整执行就判NG，不再仅为REVIEW。
2. 每个ROI主列表/主图只分配一个F汇总。内部原始检查记录以F1.1、F1.2等保留，不再在主图叠F1/F2/F3。

## NG不等于伪造局部缺陷

`Core.RequiredAppearancePolicy`在字段绑定/图像质量降级之后检查每个要求外观比对的ROI：

- 分割须为正式允许的`provisional`或`explicit_cells`且非空。
- 每个分割字块均需有真实比较结果；`compared`和已执行但`exceeds_threshold`都算“执行过”。
- 缺参考字、无字库仓库、无OCR、布局未声明、分割失败、空参考、身份不确定而跳过比较、后端遗漏该ROI或仅部分字完成，生成`appearance_incomplete / NG`。
- 已有分割失败/缺字等阻断子项也升级为NG；原消息、坐标、字块及比较记录保留。
- “未完成”是必检策略失败，不是假装找到了缺墨点；低质量不能把这个失败降回REVIEW。
- 普通未绑定字库的可选OCR不会凭空新增字库失败。已完整执行但质量不确定的视觉候选仍遵循原质量策略；OCR身份、业务内容和全局覆盖提醒保持独立。
- 原算法异常仍应按异常处理；此策略针对后端返回的缺失/未完成覆盖，不吞掉取消或任意运行时异常。

## 输出与UI

- `InspectionReport.EvidenceGroups`现在对全部ROI分组，不仅条码。`IsBarcode=false`也可以有子项，消费方不可再假设其Children为空。
- `Summary.Verdict`取NG优先、然后REVIEW、然后OK；通用汇总代码`roi_summary`，不是固定强行叫REVIEW。
- 主图只画汇总F；WinForms点击ROI汇总进入内部标记、原始明细及对应单字图块，可独立放大，双击子项定位。WPF主列表用可展开父项显示子项。
- `LocalizedCandidateCount`是不同定位候选框的数目，排除缺字/分割失败等阻断。并不是认证的真实物理缺陷数量。
- `BlockingItemCount`是阻断诊断记录数，包括汇总记录，可能描述同一失败链；不是独立故障数或缺陷点数。
- 全局图像质量、未覆盖整标签等提醒仍单独保留。
- `Analysis.Regions[].Findings`保持完整，不删证据来减少主界面行数。F编号仅在本报告内有效；JSON历史仍可读取。

## 验证

- 红测：`artifacts/required-appearance-red.log`，请求外观但无覆盖原为REVIEW、同ROI原拆成多个F。
- 新增6项测试：无覆盖NG、遗漏ROI NG、未绑定不强加失败、部分缺字NG、已执行不误标未完成、一个ROI父项完整保留子项。
- 实际OCR生产回归改为明确断言：请求外观且未完成的样本NG，其余保持原规则；OCR文本、坐标及像素比较没有放宽。
- 两框架WinForms探针验证一个NG ROI父项、全部内部子项可见，以及缺字子项仍可查。WPF构建/原有渲染工作流通过，不声称已验证物理输入。
- `artifacts/required-appearance-verification.log`：170项/框架，0失败/跳过，Release 0警告/错误；真实OCR/OpenCV和两原生UI原有流程通过。
- 合成截图：`artifacts/winforms-net48.png.quick-library.png.required-roi.png`及net8对应文件。

运行当前源码`start-ocr.cmd`使用新策略；旧dist尚未重新发布。此变更不代表工业准确率验证。
