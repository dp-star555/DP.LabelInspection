# 当前检测流程（逐ROI源码实现）

本文件描述源码，不代表旧 `dist` 已更新。原整体分析流程已归档到 `docs/decision-flow/archive-v1/CURRENT_INSPECTION_FLOW.monolithic.md`。

## 工程边界

不再保留四个旧算法工程或其程序集：`Barcode.Zxing / Ocr.Onnx / Vision.OpenCv / Vision.OnnxDetection`。标签业务装配集中到 `DP.LabelInspection.Runtime`；通用算法在 `DP.Vision.*`，实际DB候选检测也已迁到 `DP.Vision.OnnxDetection`。旧命名空间已移除，没有类型转发，宿主必须更新引用。

## 正式入口

`InspectionEngine.Inspect` → `IRoiWorkflowBackend.OpenSession` → `Core/RoiWorkflow.Run`。
内置 `OpenCvInspectionBackend.Analyze` 兼容入口也委托同一逐ROI流程，不再维护另一套文字/码混合检测流程。

对每个非 Ignore ROI：

1. 读取 `Tasks.ReadData / Tasks.CheckQuality` 和所选策略依赖。
2. 校验边界、冲突、必需的整图参考、识别器、字库固定修订等已知条件。失败记录当前ROI NG，不运行后续算法。
3. 解析本周期任务引导值或跨ROI实际读数。跨ROI依赖按需执行一次，检测循环；报告仍保持配方顺序。
4. 必要时定位。模板模式的 Translation 才执行整图ECC；Free模式不因旧默认Translation枚举值强迫加载整图参考。
5. 需要数据或质量身份时，实际OCR/读码；读不到、多码、低可信度、类型不符均阻断当前ROI。
6. 配置引导值时严格比较实际读数，再检查格式约束。不得把引导值写回识别结果，不进行O/0或大小写修正。失败不执行当前ROI质量。
7. 执行已选择的印刷质量项目，保存全部已执行检查的细节。空 findings 不能证明完成；显式未完成为NG。
8. 保存阶段轨迹，继续其余ROI。运行异常归属实际失败阶段；用户取消向上传播，不伪造完成报告。

没有有效检测ROI、检测ROI两个项目均关闭，均不得空跑后放行。正式验收不再自动发现ROI；检测器仍可用于明确的配方准备/候选发现调用。

## 数据与质量独立

| 类型/配置 | 数据 | 印刷质量 |
|---|---|---|
| 固定/空白 | 默认不适用 | 固定缺墨/多墨或空白污点 |
| 普通文字仅数据 | OCR及已配置引导/格式约束 | 未配置，不因没做外观自动降成REVIEW |
| 普通文字字库质量 | 默认策略需要实际OCR身份 | 物理分割→独立配对→单字比较 |
| 显式等格文字 | 格位标签本身不是OCR证据；可额外选择读取 | 按明确格位声明与固定修订参考比较 |
| 显式一维码仅质量 | 默认策略不要求解码 | 独立条纹/墨迹质量 |
| QR质量 | 当前实现需要解码网格结构 | 独立QR质量，不回退到一维条纹 |
| 替代整段文字质量 | 策略声明是否需要识别 | 可不依赖分割/字库；必须声明完成，不能伪造字符证据 |

引导值要求真实读取；除显式等格标签语义外，关闭读取同时保留引导/数据绑定属于配置矛盾。数据通过不认证印刷质量；质量NG也不会改写已取得的原始读取证据。

不运行强制整图清晰度/对比度门控，不把已测得的NG因为未运行整图评价降成REVIEW。报告原整图指标为NaN，UI显示“未执行”，不是零分。

## 策略与资源

- 中立接口在 `DP.Vision.Algorithms`，真实实现位于 `DP.Vision.OpenCv / Onnx / Zxing`。
- `WithQualityAlgorithms(...)` 装配固定/空白策略，支持整段文字策略和匹配器；分割、单字比较、读码、QR与一维码质量仍可分别注入。
- 算法选择由宿主代码装配；当前配方持久化数据/质量项目和参数，不包含任意插件加载器或通用模型选择UI。
- 标签业务类型仍需与中立视觉证据转换，但不再为被删除的旧工程提供程序集/命名空间兼容。**第三方仅实现旧 `IInspectionBackend` 的后端仍走历史兼容通道**；必须实现 `IRoiWorkflowBackend` 才具有这里的前置读取/质量门控保证。
- 单字库独立、ASCII大小写敏感、固定修订；补录必须人工核对发布，不自动替换参考。制库候选补切不进入正式分割。
- HALCON算法实现、ISO码评级、通用深度质量模型未提供；接口可替换不等于这些算法已经交付。

## 配置、证据与界面

`InspectionRegion.WithTasks(...)` 保存项目；旧配方缺少Tasks时从原类型/字库/等格/码印刷开关恢复默认值。显式Tasks是唯一项目依据，自动同步旧码印刷Enabled兼容字段；已删除UI重复的条码质量开关。JSON往返和ROI移动保留项目。

WinForms中文ROI编辑器提供读取/印刷质量开关；原生WPF新增单ROI项目与引导值编辑栏。WPF并非全部WinForms编辑器功能等价。

`RegionInspectionResult.Execution` 分别记录前检、读取、比较、质量：通过、失败、未配置/不适用、被阻断未执行。每ROI一个F父项，全部原始子项保留；等长内容错误可显示字符串偏移差异，但不冒充物理字符缺陷框。阻断记录与定位候选计数分开；适配器保留中立算法的显式 `IsExecutionBlocker` 角色，自定义阻断码即使带范围框/面积也不冒充印刷缺陷点。

原始图像不修改，原像素边缘坐标不改变；中立租约由结果显式拥有，旧报告适配在释放前复制图像。当前没有零拷贝、工业精度、实际相机吞吐或WPF物理输入通过的声明。

## 验证入口

- `tests/DP.LabelInspection.Tests/Workflow/RoiWorkflowTests.cs`：调用顺序、当前ROI早停、后续ROI继续、依赖、未完成、配置往返。
- `tests/DP.LabelInspection.Tests/Text/IndependentTextQualityTests.cs`：无字库/无OCR的替代质量策略以及旧Analyze入口同流程。
- `../DP.Vision/tests/DP.Vision.Algorithms.Tests/Text/TextQualityTests.cs`：真实分割/比较组合、替代匹配器、整段质量完成状态。
- 完整验收：两项目 `verify.ps1`；最新结果见各自 `VALIDATION.md`。
