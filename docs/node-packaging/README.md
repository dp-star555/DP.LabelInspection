# 标签检测节点封装资料

> 原始资料为源码盘点与建议方案。**当前已在 DP.WorkFlow 实现首版标签业务节点及 WinForms 配置/报告页面**；实际范围、宿主装配与演示以 [首版实现说明](../../../DP.WorkFlow/docs/nodes/label-inspection.md) 为准。下文仍保留最初设计背景，不将未完成的一键资源包/WPF/历史功能误标为已交付。

## 1. 已确认的目标

将当前Demo所展示的完整标签检测能力封装为**一个标签检测业务节点**，让其他使用者完成配置后直接接入流程。

- 不把OCR、条码、空白等重新拆成一批要求用户自行拼接的基础节点。
- 内部继续复用现有分阶段检测引擎、可替换算法、工作台及存储能力。
- 配置时打开工作台；正常运行只调用无界面SDK。
- 节点、运行身份、输入绑定与输出路由留在流程层。
- DP.Vision只负责图像、几何、视图集合及图层，不重新引入节点概念。

## 2. 阅读顺序

| 文件 | 内容 |
|---|---|
| [01-current-capabilities.md](01-current-capabilities.md) | 已有能力、SDK/控件/Demo分工、限制及源码证据索引 |
| [02-node-contract-and-host.md](02-node-contract-and-host.md) | 建议的节点输入输出、资源包、配置页面、运行生命周期与Workflow接入 |
| [03-packaging-checklist.md](03-packaging-checklist.md) | 缺口、实施顺序、验收用例与交付物 |

## 3. 关键结论

### 可以复用的主体已存在

- `InspectionEngine`及内置`OpenCvInspectionBackend`：无界面检测与逐ROI依赖调度。
- `InspectionRecipe` / `InspectionRequest` / `InspectionReport`：配置、请求和完整报告。
- `InspectionSourceExtensions.InspectAsync`：统一IImageSource入口及输入租约处理。
- WinForms `LabelInspectionControl`：ROI、任务、规则、绑定、试运行、证据和字库编辑入口。
- `InspectionStore`：配方JSON、固定修订字库、完整报告及导出。
- Workflow已有节点模型/Handler、强类型绑定、能力注册和隔离编辑页扩展机制。

### 不是“把Demo窗口嵌进去”就能完成

1. **Demo有宿主逻辑**：模型发现/替换、配方文件对话框、报告自动保存、批量、历史与导出并不都在工作台控件里。
2. **配方JSON不是可部署配置包**：参考图在请求中，OCR模型由宿主装配，字库只绑定ID和固定修订；单独复制JSON不保证另一台电脑可运行。
3. **两种UI并不全等**：WinForms有ApplyRecipe/CreateRequest及库管理接入；当前WPF控件没有同等的完整配方装载/导出和库管理入口。
4. **结果与故障要分层**：流程节点成功返回报告，不等于产品合格；报告的阶段Failed也不等于发生程序异常。
5. **独立业务节点**：首版已通过 WorkflowLabelInspectionModule 单独注册，并接入当前 WinForms 示例，不恢复历史拆散的OCR/切字工具箱。
6. **编辑提交桥已接入**：首版在确认前捕获完整配方到EditingNode；取消不回写。直接导出文件、发布字库修订仍是外部副作用，不随节点撤销。

### 按需缓存原型更新

Workflow生产宿主已改为启动轻量登记、选中配方首次使用时详细加载，支持空闲期限/数量淘汰和跨节点共享相同内容的OCR/CNN模型。SDK新增`LabelInspectionHost.CreateWithBorrowedModels(...)`，借用的模型不由宿主/引擎释放，调用方负责并发与租约生命周期；原独立创建入口保留。固定配方多节点分支保持兼容；Workflow现另支持单节点任务绑定ID/版本、不可变目录发布、显式刷新和生产缓存预热，SDK不负责流程配方选择。配置页试检测保持独立，多实例池/字节预算仍未实现。演示`--label-cache-demo`、`--label-recipe-demo`和当前限制见上方Workflow实际说明，不将数量限制解释为进程内存字节硬预算。

## 4. 建议的首版范围

- 对外一个“标签检测”节点，内部保持现有OCR、一维码、QR、固定参考比较、空白和忽略区域等能力。
- 一个节点对应一份固定配置及其资源引用；一次执行处理一张明确输入图像。多图循环交给外层流程。
- 优先按当前完整WinForms Demo实现功能等价的配置体验；WPF复用原生控件，但必须明确列出并补齐所需配置差异，不使用WindowsFormsHost代替。
- 首先完成无界面运行、资源装配、配置保存/加载和完整报告输出，再做标准结果视图与可选历史/导出。
- 不在首版顺带扩展旋转/尺度/透视标签配准、任意Region标签ROI、ISO码评级或新的缺陷模型。

以上保留为完整目标建议；首版交付的是无界面节点、当前资源快照、原生WinForms工作台桥及只读正式报告页，不等于全部Demo功能或可部署资源包已完成。

## 5. 资料口径

- **已有**：能定位到当前源码实现。
- **Demo宿主已有**：功能存在，但还需要从示例主程序提取或在节点宿主中重新装配。
- **待封装/待补齐**：实现节点所需的适配工作，不能当成现有API调用。
- **不在本轮范围**：算法能力扩展、工业准确率认证和新硬件接入。

原资料阶段只整理与核对引用。当前实现/回归入口见[首版说明](../../../DP.WorkFlow/docs/nodes/label-inspection.md)，不把历史统计算作本次新执行结果。SDK已有验证见[VALIDATION.md](../../VALIDATION.md)，能力边界见[当前检测流程](../../CURRENT_INSPECTION_FLOW.md)。
- [放置：由宿主定位驱动的 ROI](04-placement.md)：宿主传入配方→原图的仿射，SDK 只对 ROI 范围取样，支持平移/旋转/缩放。
