# 多实现异常检测：可切换的模型资产与运行实例

**状态：待实施设计。** 现有接口仍以Patch模型为中心；本文不是已经完成HALCON/Anomalib接入的说明。

## 1. 目标与边界

调用者只负责：选择已发布的模型资产、提交图像/检测范围、读取正式测量结果。更换实现不要求重写标签节点、良品采集、ROI、任务输入或报告页面。

“快速切换”是：新实现已登记且配套模型已制作/导入后，一次配置提交或新配方版本选择即可改变后续检测。不意味着HALCON的模型可交给Anomalib读取，不保证新算法沿用旧阈值或具有相同精度，也不承诺新模型冷加载没有延迟。

可复用图像租约、字符身份与分组、固定修订仓、坐标映射、目录发布/刷新及在途保护。不要重建通用工作流，也不要让标签SDK依赖工作流内核。

## 2. 当前代码的具体限制

| 位置 | 现状 | 迁移方向 |
|---|---|---|
| `DP.Vision.Algorithms/IPatchAnomalyDetector` | Train/Detect公开暴露具体PatchAnomalyModel | 保留旧接口，增加模型资产/运行实例的通用入口 |
| `PatchAnomalyModel` | DPPA格式及浮点记忆库 | 作为旧Patch实现内部格式，不成为所有厂商的模型格式 |
| `DP.LabelInspection.Runtime/AnomalyModelCache` | 强制PatchAnomalyModel.FromBytes | 由匹配实现解析，并管理原生实例租约 |
| `CharacterAnomalyDetector.Operator` | 直接创建OpenCV字符算子 | 提取共用制作/几何流程；替换测量实现，不再写死OpenCV检测器 |
| `RegionAnomalyDetector` | 训练/检测参数都为PatchAnomalyOptions | 业务组织保留，厂商参数由实现解释 |
| `WorkflowLabelInspectionModels` | ONNX文件名、CNN来源和缓存角色写死 | 按实现、完整模型资产及执行环境装配 |
| `AnomalyModelEntry` | 已有不透明字节，但元数据仍偏Patch且单模型64MB | 新资产格式采用显式schema、预算和兼容信息；旧修订不改写 |

## 3. 比较后的接口选择

1. **继续扩展IPatchAnomalyDetector**：改动少，但会迫使HALCON原生模型伪装成记忆库；参数和生命周期持续泄漏。拒绝。
2. **一个万能Train/Detect接口**：看似简单，但推理-only ONNX实现没有训练能力，外部Python训练与本机推理也会被迫绑定。拒绝强制组合。
3. **模型资产连接可选训练与必需运行实现**：选择此方案。公共接口少，模型兼容、加载、厂商参数与原生生命周期集中在实现内部。

不向业务层暴露HObject、HTuple、PyTorch Tensor或ONNX Session。训练工具和运行引擎分别登记，但由资产清楚说明目标运行实现。

## 4. 模块责任与扩展接口

### 4.1 运行实现登记

每个异常检测实现提供稳定`ImplementationId`、接口版本、可读模型格式、输入约束、能力和执行共享方式。品牌是显示信息，不是兼容判据。

示例ID（设计示例，不是已注册功能）：

- `opencv.patch-handcrafted`：现有Patch实现。
- `halcon.anomaly-detection`、`halcon.gc-anomaly-detection`：HALCON原生异常模型。
- `onnx.patchcore-anomalib`：Anomalib导出的PatchCore完整推理资产。
- `openvino.efficientad-anomalib`：另一种部署实现。

Anomalib是训练框架，不能用`anomalib`一个名字掩盖实际算法、导出格式和运行方式的差异。登记阶段仅提供元数据/工厂，不加载原生SDK、不检查许可、不占GPU。

### 4.2 小接口示意

以下为设计草图，名称与签名在实施时按现有Vision工厂契约调整，尚未加入公开程序集。

```csharp
// 必需：解释资产、验证环境并创建可释放的运行实例。
interface IAnomalyImplementation
{
    AnomalyImplementationDescriptor Descriptor { get; }
    Task<ILoadedAnomalyModel> LoadAsync(
        AnomalyModelSnapshot model, AnomalyRuntimeOptions execution,
        CancellationToken cancellationToken);
}

interface ILoadedAnomalyModel : IDisposable
{
    Task<AnomalyMeasurement> InspectAsync(
        AnomalyInput input, CancellationToken cancellationToken);
}

// 可选：同进程HALCON训练，或外部Anomalib训练/导出Adapter。
interface IAnomalyTrainer
{
    AnomalyTrainingDescriptor Descriptor { get; }
    Task<AnomalyBuildArtifact> TrainAsync(
        AnomalyTrainingRequest request, CancellationToken cancellationToken);
}
```

登记、环境检查、模型解析和执行通过现有Vision能力/工厂体系适配，避免再创建一套竞争的插件加载系统。允许只有推理实现，工作台此时提供“导入模型”，不展示无效训练按钮。HALCON训练和Python训练的外部依赖由各自Adapter管理。

### 4.3 公共输入

- 不可变图像租约、明确裁图/行ROI、到原图的可逆映射。
- 训练输入包含稳定来源/批次标识，而不是仅把字符切片当独立来源。
- 逐字符模式提供精确Unicode身份及字体组；不是所有模型都必须依赖OCR。
- 输入尺寸、通道顺序、数值范围、缩放/补边及几何归一化版本属于资产契约。
- 共用单字组织逻辑不等于强制所有实现用61像素高单元；输入约束不兼容时明确拒绝或采用资产声明的制作流程，禁止检测时临时猜测缩放。

## 5. 完整、不可变的模型资产

资产包含manifest及一组相对路径文件，内容寻址和固定修订保留。公共层不解析模型内部权重。

manifest至少记录：

- 目标ImplementationId、兼容接口版本、模型格式及格式版本。
- 实际算法、训练实现与版本；运行环境约束，必要的HALCON版本/许可或ONNX opset。
- 文件清单、长度、SHA256、用途；单文件和总包预算。
- 输入规格及预处理/几何契约；训练参数和来源统计。
- 模型输出定义；图像级及像素级阈值、标定协议与验证记录。
- 适用范围：整ROI或字符组/单字；不能把空白/组合序列变成独立参考身份。

HALCON资产可包含原生模型及配套预处理字典；Anomalib资产可能是完整ONNX推理图，也可能需要额外记忆库/分布参数。不能假设“一个ONNX骨干文件”已包含完整异常模型。

加载前核验schema、接口/格式、所有摘要、根内路径及资源预算。禁止绝对路径、越界相对路径和符号链接/ReparsePoint绕过。未知实现、许可缺失、缺文件或不兼容明确失败，不自动退回OpenCV。

旧库v1/v2和DPPA模型保留，通过LegacyPatch Adapter读取；新资产使用新的显式schema，旧SDK应拒读。不能扩展旧schema然后让旧运行时忽略关键字段。新训练发布新修订，不覆盖历史文件。

## 6. 结果一致，算法分数不假装一致

公共结果保留执行完成性、异常区域、原图坐标、模型/实现身份、原始分数及阈值、可选分数图/显示热力图和诊断证据。

- NG/Review属于有效产品报告；程序错误、许可错误、能力缺失不伪装成局部印刷缺陷或OK。
- 图像级与像素级判定分别描述；GC-AD的局部/全局证据可分别保留，不能用一个最大块距离丢掉全局判定。
- HALCON分数、PatchCore距离和PaDiM马氏距离不能共用数值阈值。显示用阈值倍数不等于概率；各模型之间不能直接按分数排序。
- 热力图128对应阈值仅为显示约定，不反过来参与真实判定。
- 空findings不是完成性证明；要求定位却只有整图分数时应明确能力不足。
- 原始`ink_loss`是旧手工模型的可选附加能力，不自动叠到HALCON/Anomalib上。配方要求该能力但实现不支持时提示/拒绝，而不是静默略过或假称已覆盖。

## 7. 一次切换的完整路径

1. 新实现及其依赖已部署、登记；工作台显示能力与可用资产，不在启动时加载所有模型。
2. 使用同一批良品训练或导入另一实现的完整模型资产，完成独立精度验证，再发布固定修订。
3. 工作台选择“实现+兼容库修订/模型键”。先检查算法输入、ROI、字符覆盖、预处理、阈值、许可及必检能力，再原子提交绑定。
4. 冻结计划后不修改全局当前实现。目录配方发布更高版本、显式刷新；每周期按任务选择配方版本即可在同一标签节点使用不同实现。
5. 请求开始时固定具体模型资产/实现快照。刷新或切换不改变在途A；下一请求可加载B。
6. 回切选择历史已验证配方/模型修订，不逆转换资产、不删除新旧模型。

示例：HALCON配方A正在检测，后台发布Anomalib配方B并刷新。A继续使用自己的HALCON实例；下一周期选择B使用对应ONNX实例。如果B加载失败，报告明确故障，不拿A的结果冒充B。

快速切换的前提是已有兼容且验证过的模型；首次冷加载仍有成本，可预热。不能保证零训练、零部署、零延迟。

## 8. 缓存与原生生命周期

缓存身份至少为ImplementationId/兼容版本、完整资产摘要、预处理契约、CPU/GPU设备及影响行为的运行参数。不能只以文件名、一个权重SHA或`cnn:hash@2`识别全部实例。

保留TTL/LRU/数量/并发加载预算、同键合并加载、独立等待取消和租约保护。新旧实例及加载工作区都占预算；在途/排队资源不能淘汰。多文件资产和原生/GPU内存需额外统计，数量预算仍不是进程内存硬上限。

运行实现声明共享方式：单实例串行、可并发或需要实例池；默认不能猜测厂商对象线程安全。原生加载实例由缓存拥有，调用者只借用租约。最终释放顺序为检测/排队结束、引擎退出、归还模型租约、厂商句柄清理、临时资源清理。

无法中断的原生调用只能等它返回后处理取消，不提前销毁句柄。迟到结果不发布。外部训练只管理自己创建的进程，不强杀用户调试或其它应用。

## 9. 实施顺序

1. 公共资产/测量/运行实例契约及登记，LegacyPatch Adapter；旧数据和行为回归，不删除原接口。
2. 替换SDK强制Patch解析与OpenCV检测创建；模型库、训练页、结果显示增加实现/资产兼容选择。
3. 工作流资源/profile/缓存身份支持新资产包，保留根检查、固定版本和冻结计划。
4. HALCON Adapter实现原生加载、预处理、测量与释放；许可/版本测试独立执行。
5. Anomalib导入及ONNX/OpenVINO Adapter；训练可离线，不强迫生产.NET进程安装Python/PyTorch。
6. 同数据对照精度、性能和资源压力，再启用生产切换。

在资产协议和预处理固定后，新增实现的常规步骤应为：实现Adapter、登记描述/工厂、制作或导入兼容资产、通过契约测试。无需修改标签节点Handler或报告布局。

## 10. 验收条件

- 同一公共接口覆盖LegacyPatch及至少一个真实新实现；假Adapter只验证协议，不代表厂商接入完成。
- 原生资产往返、旧修订可读、未知schema/实现拒绝、篡改/路径/预算失败。
- 训练与运行预处理一致；不同实现不串阈值、不串字符组、不串图像坐标。
- 同节点A(HALCON)→B(Anomalib)→A，旧在途报告身份不变；未知B不回退A。
- 原生实例只释放一次；取消、关闭、TTL/LRU及容量耗尽时无Use-after-free。
- 无SDK/许可也能登记未选实现；选择后明确拒绝，不阻断其它配方的轻登记。
- 独立正常验证来源衡量误报率，缺陷样本只用于检出评估；整张标签和逐字符指标分开统计，多个字符判定的累积误报不能忽略。
- 测试异常数据不混入良品训练；标定数据、训练数据及测试数据分组隔离，不以复制样本证明泛化。

本设计解决可替换性，不自动解决误判；现场原图、采集集和正式报告仍是精度验收的必要输入。
