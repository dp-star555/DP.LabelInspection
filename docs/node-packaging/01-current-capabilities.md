# 01 · 当前能力与实际复用入口

本文是当前源码盘点。文末S编号为证据索引，后续设计不得用建议接口替代这些实际入口。

## 1. 检测能力

| 用户能力 | 当前实现与输入条件 | 封装时必须保留的语义 |
|---|---|---|
| OCR文字 | Text ROI、单行识别、预处理、ONNX推理、CTC解码；可配置Expected、正则、字符集和长度 | 实际读值独立保存，不用期望值改写O/0、大小写或漏字 |
| 文字印刷质量 | 物理分割、字符配对、固定版本字库比较及可替换整段质量策略 | 读取/内容比较/质量分别记录；单字参考比较按实际配置启用，不把未绑定字库自动当成必须制库 |
| 一维码 | Barcode ROI选择一维码，解码、内容规则、独立条/空隙质量检查 | 能读码不等于印刷质量合格；保留实际读值和局部缺陷 |
| 二维码 | QR解码及模块/结构相关质量检查 | 不宣称ISO等级；不能把其他码制能解码等同于其外观质量已实现，DataMatrix外观不在已交付范围 |
| 固定图案/固定文字 | Fixed ROI、参考图像素差异、缺墨/多墨及容差 | 当前所谓模板检测主要是固定参考比较，不是任意姿态模板搜索 |
| 标签对齐 | 上游显式声明已对齐，或受限ECC平移配准 | 当前最大平移12px；不自动处理旋转、尺度、透视，不用随机Value对齐 |
| 空白 | Blank ROI上的不允许墨迹/污点检查 | 原图位置、面积、阈值和独立结果保留 |
| 忽略区域 | Ignore ROI参与忽略配置，不作为有效验收任务 | 全部忽略/没有有效检查不能空跑放行 |
| 动态内容 | FieldBinding关联其他文字/码ROI或TaskDataSnapshot字段 | 验证采集周期、有效期和唯一读数；跨ROI一致性不是独立业务真值 |
| 作者辅助 | DB文字候选、多图制库、手工补框/拆分、确认及发布新字库修订 | 候选是配方准备材料，不是正式验收事实；不自动升级已绑定字库 |

依据：S01、S02、S03、S04。注意：公共DP.Vision中已有其他模板定位算子，不代表它们已自动接入当前标签检测请求。

### 当前运行约束

- 标签源入口支持Gray8/Bgr24，边长≤12000、总像素≤1600万；不静默降位深或缩放。需要转换时由外层显式处理。
- 配方绑定固定宽高，最多128个具名区域、最多256个字段绑定。
- 当前单字库面向ASCII字母/数字，每字符每类别一个参考，字块4～512像素；这与OCR模型自身支持的字符集是不同限制。
- 标签业务ROI仍是`PixelRect`。不要从通用Region/旋转多边形取外接矩形，冒充原范围已经支持。
- `RoiInspectionTasks.ReadData/CheckQuality`是正式项目选择；不能因为旧Enabled开关或缺少算法而悄悄撤销已选任务。
- 内置后端进入Core分阶段流程；第三方仅实现旧`IInspectionBackend`时仍可能走兼容路径，不能宣称所有后端语义完全相同。

## 2. 无界面SDK入口：已有

| 类型/入口 | 实际作用 | 所有权与注意事项 |
|---|---|---|
| `InspectionRecipe` | 尺寸、参考模式、对齐、Regions、Options、Bindings的不可变配置 | 不包含参考图像、模型文件或字库文件实体 |
| `InspectionRequest` | Actual、Recipe、Reference、CycleId、TaskData | Actual/Reference是标签自己的不可变图像快照，不是DP.Vision.ImageFrame |
| `IInspectionEngine.InspectAsync(InspectionRequest, token)` | 正式无UI执行入口 | 不调用工作台；依赖引擎由宿主装配 |
| `InspectionSourceExtensions.InspectAsync(engine, IImageSource, recipe, reference, cycleId, taskData, token)` | 更适合节点的统一图像源入口 | 返回任务前保留输入源，内部复制为标签快照；任务结束释放内部租约 |
| `InspectionEngine(backend, ownsBackend=false)` | 请求校验、同引擎串行执行和Core判定 | 引擎可共享，但同实例并不并行；Dispose可能等待执行结束 |
| `OpenCvInspectionBackend` / `WithQualityAlgorithms` | 装配识别器、读码、字库、候选及质量策略 | 默认不拥有注入识别器/检测器，只有明确owns参数才转移释放责任 |
| `InspectionReport` | Verdict、Analysis、Findings、EvidenceGroups、耗时和后端名 | 没有一个已经定义好的全局“检测完全执行”布尔字段 |

依据：S01～S07。正常执行无须先建立窗体、模拟点击“开始检测”或读取控件里的上一次结果。

## 3. 当前WinForms控件能直接复用什么

类型：`DP.LabelInspection.LabelInspectionControl`，见S08。

| 已有成员 | 节点配置页中的用途 |
|---|---|
| `AttachEngine` | 注入宿主创建的引擎，控件不拥有引擎 |
| `AttachLibraryManager` | 连接固定修订字库管理器及工作台库编辑入口 |
| `SetActualImage(source, clearRegions=true)` | 设置调试图；内部复制图像，调用后可释放客户源 |
| `SetReferenceImage(source, assumeAligned=false)` | 设置或清空参考模式 |
| `ApplyRecipe(recipe)` | 应用配置；必须先加载尺寸正确的实际图，模板模式还必须有参考图 |
| `CreateRequest()` | 捕获当前配置、实际图、参考图和任务数据 |
| `SetRegions` / `Regions` | 区域配置导入与快照读取 |
| `SetBindings` / `SetTaskData` | 稳定绑定配置及本次调试数据 |
| `RunInspectionAsync` | 配置页试运行；必须在UI线程调用，不作为生产节点入口 |
| `LastRequest` / `LastReport` / `InspectionCompleted` | 当前工作台试运行记录与事件 |
| `CancelAndWaitAsync` | 关闭页面或更换服务前取消并等待检测 |

控件内部还包含规则编辑、字段绑定、任务数据调试、ROI手势、单字证据、补库等入口，可复用，而不是全部重画一套配置界面。

**需要注意的实际行为：**

1. `SetActualImage`总会清空旧TaskData/CycleId；保留ROI时须显式传`clearRegions:false`，然后再注入本次任务数据。
2. `CreateRequest`生成配方名为`WinForms configuration`；它不是完整的外部配置身份导出器，节点不能因此覆盖原配方名称/版本/资产身份。
3. `ApplyRecipe`清空调试任务数据；调用顺序建议为实际图 → 必要参考图 → 配方 → 本次任务数据。
4. 控件没有现成的“节点确认时把配置写入EditingNode”的提交桥，需要补。
5. 库管理写入会发布外部修订；取消节点编辑并不会自动撤回已经发布的字库，需要单独说明副作用与确认边界。

## 4. WPF：已有原生控件，但配置能力不全等

类型：`DP.LabelInspection.Wpf.LabelInspectionControl`，见S09。

已有AttachEngine、源输入、SetRegions、ConfigureRoiTasks、SetBindings、SetTaskData、RunInspectionAsync、InspectionCompleted、CancelAndWaitAsync及Dispose。

当前没有与WinForms同等的`ApplyRecipe`、`CreateRequest`、`LastRequest/LastReport`公共访问和`AttachLibraryManager`入口。运行方法直接从内部区域生成名为`WPF`的请求，Options通过当次调用提供；完成事件携带Request和Report。

因此：

- 原生显示、ROI及试运行可以复用；不能宣称现有完整WinForms编辑体验已经可以原样用于WPF节点页。
- 如要两宿主同时提供相同配置能力，应补共享的配方装载/捕获/提交语义，并补齐WPF所需作者操作。
- 不用WindowsFormsHost嵌套WinForms当作“原生WPF已完成”。

## 5. 哪些能力实际在Demo宿主

WinForms `samples/.../Runner/Program.cs`，见S10：

| Demo宿主已有功能 | 封装去向 |
|---|---|
| 创建codec/store/backend/engine及模型热替换 | 宿主运行服务/模型资源装配，不放节点持久化对象构造函数 |
| 环境变量、工程目录、原型目录中寻找模型和样例 | 改为显式部署资源配置；不要携带开发机器兜底路径 |
| 安装生产种子库、构造合成样例 | 演示或明确的安装工具，不作为正式节点默认副作用 |
| 保存/载入配方文件对话框 | 配置页工具栏及资源包导入导出 |
| 监听InspectionCompleted后Task.Run保存报告 | 独立的宿主报告策略，不误以为引擎自动保存 |
| 报告保存失败的提示、等待未完成保存任务 | 宿主生命周期与存储故障策略 |
| 历史/人工复核/ZIP导出 | 独立查询/证据工具，可从节点结果页进入 |
| 逐图批量 | Demo便捷功能；正式批次建议由流程循环组织 |
| FormClosing取消检测并等待保存队列 | 节点配置页面和宿主退出时的清理适配 |

**不能仅嵌入一个LabelInspectionControl就声称已经复现整个Demo。**

## 6. 持久化现状

见S11：

- `SerializeRecipe`只序列化配置；`DeserializeRecipe`限制JSON长度并通过构造函数校验。
- `InspectionRecipe`只有字库ID/固定修订等引用，模型由外部组装，不在配方里。
- `SaveReport(request, report, annotated)`才会按事务目录保存配方、任务上下文、报告、actual.png、可选reference.png、标注图和相关字库证据。
- 报告ZIP是已执行任务的证据导出，不能直接当成尚待定义的可部署节点配置包。
- `InspectionStore`提供库读取/编辑及报告存储，但不是一个现成的“节点资源包加载器”。

## 7. 源码证据索引

路径均相对`DP.LabelInspection`目录。

| 编号 | 当前源码 |
|---|---|
| S01 | [Core引擎](../../src/DP.LabelInspection.Core/Inspection/InspectionEngine.cs)、[逐ROI调度](../../src/DP.LabelInspection.Core/Inspection/RoiWorkflow.cs) |
| S02 | [运行时组装](../../src/DP.LabelInspection.Runtime/Inspection/OpenCvInspectionBackend.cs)、[ROI会话](../../src/DP.LabelInspection.Runtime/Inspection/OpenCvInspectionBackend.OpenCvRoiSession.cs) |
| S03 | [配方](../../src/DP.LabelInspection.Contracts/Inspection/Configuration/InspectionRecipe.cs)、[任务选择](../../src/DP.LabelInspection.Contracts/Inspection/Configuration/RoiInspectionTasks.cs) |
| S04 | [阶段记录](../../src/DP.LabelInspection.Contracts/Inspection/Execution/RoiExecution.cs)、[阶段枚举](../../src/DP.LabelInspection.Contracts/Inspection/Execution/ERoiStageState.cs)、[证据角色](../../src/DP.LabelInspection.Contracts/Inspection/Results/InspectionFinding.cs) |
| S05 | [请求](../../src/DP.LabelInspection.Contracts/Inspection/Configuration/InspectionRequest.cs)、[任务数据](../../src/DP.LabelInspection.Contracts/Rules/TaskDataSnapshot.cs) |
| S06 | [源输入扩展](../../src/DP.LabelInspection.Adapter.Vision/Algorithms/InspectionSourceExtensions.cs) |
| S07 | [报告](../../src/DP.LabelInspection.Contracts/Inspection/Results/InspectionReport.cs) |
| S08 | [WinForms工作台](../../src/DP.LabelInspection/Workbench/LabelInspectionControl.cs) |
| S09 | [WPF工作台](../../src/DP.LabelInspection.Wpf/Workbench/LabelInspectionControl.cs)、[完成事件](../../src/DP.LabelInspection.Wpf/Workbench/WpfInspectionCompletedEventArgs.cs) |
| S10 | [WinForms Demo宿主](../../samples/DP.LabelInspection.Demo.WinForms/Runner/Program.cs)、[WPF Demo宿主](../../samples/DP.LabelInspection.Demo.Wpf/Runner/Program.cs) |
| S11 | [库/配方/报告存储](../../src/DP.LabelInspection.Storage/Persistence/InspectionStore.cs) |
| S12 | [显示转换](../../src/DP.LabelInspection.Adapter.Vision/Display/VisionAdapter.cs) |

上述链接从本文所在目录解析。功能政策还可查阅[DATA_BINDING](../../DATA_BINDING.md)、[ROI参数](../../ROI_PARAMETER_GUIDE.md)、[多图制库](../../QUICK_GLYPH_LIBRARY.md)、[条码](../../BARCODE_PRINT.md)、[QR质量](../../QR_PRINT.md)。
