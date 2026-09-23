# 02 · 标签检测节点契约与宿主接入方案

> 本文为建议方案。除明确标为已有的SDK/Workflow类型外，新节点、资源包、摘要与页面适配均尚未实现。

## 1. 封装的对象

一个“标签检测”节点 = **输入绑定 + 固定配置/资源解析 + 无界面调用 + 报告输出 + 专用配置页面**。

内部继续使用InspectionEngine，不在节点Handler中重新实现逐ROI调度，不要求使用者手工串接OCR、解码、字形比较和空白算子。

职责建议：

| 位置 | 应负责 | 不应负责 |
|---|---|---|
| LabelInspection SDK | 检测配置、算法装配、逐ROI政策、原始报告、标签领域的完成性语义 | Workflow端口、NodeId和设计器会话 |
| 标签节点适配模块 | 模型、Handler、能力声明、输入解析、结果关联、页面注册 | 复制算法、通过UI按钮执行生产检测 |
| 宿主装配/资源服务 | 模型与参考资源加载、固定版本核验、引擎缓存与释放、存储策略 | 从上一帧或开发目录悄悄补缺失输入 |
| 专用配置页面 | 承载工作台、调试、配置捕获及节点编辑提交 | 修改正在执行的配置、取消编辑却写回正式节点 |
| DP.Vision | IImageSource、几何、VisionView及图层显示 | 标签业务判定、节点状态或运行批次 |

## 2. 建议的节点输入

| 输入 | 建议形式 | 约束 |
|---|---|---|
| 待检图 | `WorkflowInput<DP.Vision.ImageFrame>` | 与现有Workflow图像输出对齐；运行帧只允许绑定，不保存为Literal |
| 检测配置 | 固定配置包引用/版本，或节点内保存的配方JSON及固定资产引用 | 不能引用“最新版本”而不记录实际版本；一次执行捕获一致快照 |
| 参考图 | 首版建议由配置包解析 | 只有确实需要模板模式时要求；如果以后支持上游参考绑定，明确它与包内参考的互斥/优先规则 |
| 动态期望数据 | 可选`WorkflowInput<TaskDataSnapshot>` | TaskData绑定被使用时必须提供正确周期和有效期，不能从OCR结果生成 |
| 采集周期 | 明确绑定/提供的CycleId | 不默认等同Workflow RunId或NodeExecutionCount；一次运行可能处理多张图 |
| 取消 | Handler收到的CancellationToken | 贯穿资源读取及引擎调用，不能转换成合格结果 |

节点持久化对象不得保存Control、引擎、ONNX会话、图像句柄、完整运行报告或本次TaskData。

### 现有SDK可直接调用的链路

以下只展示已有入口，不是完整Handler实现：

```csharp
// using DP.LabelInspection.Adapter.Vision;
// engine、recipe、referenceSource由宿主能力及资源解析得到；frame为绑定得到的DP.Vision.ImageFrame。
var report = await engine.InspectAsync(
    frame.Image,
    recipe,
    reference: referenceSource,
    cycleId: captureCycleId,
    taskData: taskData,
    cancellationToken: cancellationToken).ConfigureAwait(false);
```

图像源扩展在返回任务前保留输入，内部仍转换成独立标签图像快照。若节点在调用它之前还要await资源解析，应先保留必要的输入帧租约，不能跨异步边界依赖别人随时可能释放的借用句柄。不要额外在Handler里创建工作台，也不要把DP.Vision.ImageFrame与标签Contracts.ImageFrame混为同一种类型。

**报告保存还有一个接入点：**源扩展只返回Report，不公开其内部InspectionRequest；而InspectionStore.SaveReport需要当次Request。启用保存时，宿主执行服务应持有与检测完全相同的请求快照，例如从保留的源构造一次标签快照和InspectionRequest，再调用已有的`engine.InspectAsync(request, token)`并保存该请求/报告。也可显式保留原源再复制，但必须说明额外复制成本。不能从UI.LastRequest、当前选中图或另一周期重建保存输入。

## 3. 输出与流程语义

### 首版必须保留的输出

建议用一个强类型结果对象封装：

- 原始`InspectionReport`，完整保留Verdict、Analysis、Findings、EvidenceGroups。
- 输入FrameId及实际使用的配置版本/资产摘要，便于确认报告属于哪张图及哪份配置。
- 必要的宿主执行关联：NodeId与WorkflowExecutionIdentity由外层记录，不塞回Vision。
- 可选报告存储标识及存储状态；存储失败不能改写检测报告。
- 便捷字段可转发Verdict、耗时、各ROI实际读值和状态；多码候选不能无条件取第一项充当唯一读值。

`IsQualified`如需提供，只转发SDK明确的Ok语义，不自行重算检测结论。必须同时保留详细报告，false不能单独解释为“已完整测量且发现实体缺陷”。

### 三层“完成”不能混用

1. **节点契约完成**：Handler成功返回了约定的标准输出。
2. **所选检查覆盖完成**：需要执行的读取/比较/质量阶段是否确实完成或被阻断。
3. **业务合格**：SDK给出的Verdict是否为Ok。

现有`ERoiStageState.Failed`同时表示条件缺失、执行失败、内容不符或质量缺陷；`RoiWorkflow`也可能捕获ROI级异常，继续其他ROI并最终返回报告。因此：

- 不能以“没有抛异常”推断所有检查完成。
- 不能以“所有阶段都Passed”定义检测完成，合法检出的NG也可能是完整测量。
- 不能以“没有局部缺陷框”或“没有IsExecutionBlocker”推断全部完成。
- 不能解析Message里的中文字样做路由。

**待补齐的领域契约：**如果节点要提供明确的`AllRequestedChecksCompleted`等摘要，应先在标签领域层定义并补齐无歧义的执行证据，再映射到节点；不要在Workflow中建立另一套靠状态/字符串猜测的判定规则。首版在完成该项前应保留阶段记录及原因，不伪造该布尔值。

### 建议的默认路由

| 情况 | 节点/流程处理建议 |
|---|---|
| 返回有效报告，Verdict=Ok | 正常输出报告，经Success继续；Success指契约完成 |
| 返回有效报告，Verdict=Ng或Review | 仍正常输出报告；后续条件按Verdict处理产品，不能自动当设备异常重试 |
| 返回报告但含检查阻断/ROI执行问题 | 输出原报告及明确诊断；与完整检测NG区分，不伪造产品合格；是否停止/转人工由外层策略决定 |
| 输入绑定错误、资源加载异常、引擎未能返回报告 | 进入节点故障机制，不复用上一次输出 |
| 用户/流程取消 | 传播取消，不生成假报告，不无条件重试 |
| 报告保存失败 | 保留已有检测事实，另报存储失败；按宿主要求决定是否阻止后续放行 |

建议首版只保留现有标准Success出口，由下游条件节点处理业务判定。若之后要OK/NG/未完成专用端口，先定义其严格语义及未连接端口规则，不把所有非Ok都塞进程序Fault。

## 4. 可部署配置不是一份recipe.json

建议定义带版本的资源清单，至少包含：

- 配方JSON及配置SchemaVersion、稳定ID、固定版本或内容摘要。
- 模板模式需要的参考图及摘要；明确其尺寸与坐标约定。
- 字库ID、**指定Revision**以及该修订的可解析资源/摘要，不使用Latest代替。
- 实际使用的识别/检测模型标识、版本、哈希及提供方式；不将OCR设为纯空白检查的新强制门控。
- 可选作者样张，用于打开配置页；与生产待检图、正式报告严格分开。
- 对应SDK/运行时版本及允许的输入布局、尺寸。

包可以携带获准分发的资源，也可以引用宿主的受控资源仓；两种方式都要能核验并清楚报告缺失。相对路径应限定在受控根目录，不接受任意越界路径、自动联网下载或开发目录兜底。

配置导入不得静默重绑定库ID/Revision。当前库导入会创建新ID，打包方案必须定义显式映射和用户确认，而不是直接假定原ID在另一台机器存在。

模型、生产样张、参考图和字库的分发许可/隐私需独立审查。默认不把开发机生产数据和历史报告带给其他使用者。

## 5. 配置页面接入

### 已有Workflow接入点

- `IWorkflowNodeEditorPageProvider` / `WorkflowNodeEditorPageDescriptor`：贡献专用页面及RendererKey。
- `IWorkflowWinFormsNodeEditorPageRenderer`与WPF对应Renderer：原生宿主分别创建控件。
- `WorkflowNodeEditorModel.EditingNode/EditingSession`：隔离编辑副本。
- `WorkflowNodeEditorModel.ApplyChanges()`：整体提交到正式节点。
- 页面模型可实现`IAsyncDisposable`，用于关闭时取消并等待工作台试运行后释放UI资源。

### 建议的编辑顺序

1. 从EditingNode读取固定配置引用，解析作者样张/参考图/模型和库。
2. 创建页面拥有的工作台，注入宿主管理的引擎与库服务。
3. 加载实际调试图 → 必要参考图 → 配方；调试TaskData最后注入。
4. 用户编辑及试运行只作用于草稿和调试状态，不更新流程的生产标准输出。
5. 确认前捕获配方快照，保留外层配置名称、身份及资源引用，校验后更新EditingNode。
6. 由Workflow正常ApplyChanges提交；取消则丢弃草稿。已显式发布的外部字库修订另行告知，不冒称可随节点取消回滚。
7. 页面关闭先异步停止试运行，再在正确UI线程释放控件。共享引擎只有其所有者可释放。

**目前尚无通用的工作台配置提交桥。** WinForms CreateRequest依赖已加载的图像且会生成固定名称，WPF又没有同等接口；不得把仅Regions复制出来当成完整配方保存。现有Workflow ApplyChanges直接捕获EditingNode，不会自动从标签控件拉取配置。因此必须让作者编辑可靠同步到草稿模型，或补充通用的确认前提交机制；不能将回写放进Dispose，因为取消关闭也会释放页面。

避免继承`AnalyzeVisionFrameNodeModel`只为复用其通用ROI页面：它携带FullImage、通用Region/Mask、定位坐标绑定，语义不同于当前标签PixelRect配方。建议专用标签节点直接使用Workflow标准节点模型和专用工作台页面。

## 6. 注册与目标框架

当前Workflow已有：`WorkflowNodeDescriptor.Create<TNode,TOutput>`、`WorkflowNodeHandler<TNode>`、`WorkflowInput<T>`、`GetRequiredCapability<T>`、`WorkflowRuntimeCapabilityRequirement.Require<T>`和插件模块注册。

建议新增专用模块，而不是把标签业务放进DP.Vision或修改通用ImageNodes目录：

| 建议模块（未创建） | 职责 |
|---|---|
| `DP.WorkFlow.Nodes.LabelInspection` | 模型、Handler、标准输出、预检/能力声明及节点注册 |
| 标签节点宿主装配 | 实际资源解析、运行时/模型/存储能力注册 |
| 共享标签节点编辑模型 | 草稿、资源状态、试运行和提交协调 |
| WinForms/WPF标签节点页面Renderer | 承载各自原生工作台并处理UI生命周期 |

现有Workflow节点项目以net8.0为主；Label Contracts/Core/Storage/Adapter为netstandard2.0，实际Runtime和控件为net48/net8.0-windows。纯节点契约层可以引用可移植契约/适配器并依赖宿主能力，Windows装配层才引用实际运行时和UI。不要给整个Workflow内核增加Windows或标签算法依赖，也不要承诺现有Workflow本身支持net48。

## 7. 生命周期与结果显示

- 一个引擎实例串行执行；并行节点是否共享、等待或使用独立实例，由宿主明确。不要每张图重复加载模型。
- 配方/库/参考更新不能修改进行中的检测快照；运行中使用固定版本。
- 原生算子采用协作取消，超时不意味着底层工作已经退出。未退出前不能释放模型、参考或图像资源。
- 正式输出、已选节点预览和报告持久化分别管理；图像帧作用域预算不覆盖标签报告中所有复制的字符/差异图，需要另定报告历史保留策略。
- 若输出会经过通用JSON序列化，须验证强类型恢复、字段绑定和NaN等数值；当前分阶段报告的全图质量数值可能是NaN，不能为了序列化改成伪造的0或通过值。

显示侧可复用`VisionAdapter.LabelLayers/ConvertGeometry`与`VisionView`，但尚无一个“完整报告→全部多视图”的成品工厂。建议原图结果、参考图及按需字符/差异视图分开提供：

- 参考ROI显示到实际图时考虑Report.Analysis.OffsetX/OffsetY，保持现有工作台映射规则。
- 局部字符/归一化差异图使用各自FrameId和局部坐标，不直接叠原图范围。
- 缺陷、执行阻断、全局说明分别呈现；不要把有Bounds的诊断都当作局部印刷缺陷计数。
- 外层预览拥有节点/执行关联及迟到结果过滤，之后调用Vision的SetViews；预览被拒绝不影响正式报告，不重跑算法。
- 现有WorkflowVisionFrameScope.Publish是internal且其事实类型/页面转换针对现有视觉结果；独立标签模块不能假定直接调用即可接上，需新增公开适配契约或专用预览提供者。

## 8. Workflow源码依据

以下路径相对工作区根目录：

- `DP.WorkFlow/src/Workflow/Kernel/DP.WorkFlow.Abstractions/Execution/{IWorkflowNodeHandler,NodeExecutionResult,WorkflowExecutionIdentity}.cs`
- `DP.WorkFlow/src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Catalog/WorkflowImageRuntimePluginModule.cs`
- `DP.WorkFlow/src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Tools/AnalyzeVisionFrameNodes.cs`
- `DP.WorkFlow/src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Acquisition/WorkflowVisionFrameScope.cs`
- `DP.WorkFlow/src/Workflow/Studio/DP.WorkFlow.UI.Shared/Editors/WorkflowNodeEditorModel.cs`
- `DP.WorkFlow/src/Workflow/Studio/DP.WorkFlow.Vision.UI/Editors/VisionFrameEditorPage.cs`
- `DP.WorkFlow/src/Workflow/Studio/DP.WorkFlow.Vision.UI.WinForms/Editors/VisionFrameEditorRenderer.cs`

这些现成机制可以作为参考，但并不等于标签节点已注册或当前通用预览仓已支持标签全部证据。
