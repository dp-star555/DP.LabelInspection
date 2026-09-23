# 验证记录 · 0.2.0-preview.1

## 最新：上游浏览器收敛为纯视图

DP.Vision移除节点、流程运行身份与执行状态，仅提供VisionView视图集合及图层浏览。标签业务代码不依赖被删除的契约，工作台布局、逐ROI阶段、原始证据及字库规则未改。

两框架各 **219项通过**，完整OCR/外观/DB发现及原生工作台回归通过；52行Python/CTC一致52、人工精确51，375字符/370比较/5缺失/1超限和37个作者候选保持原语义。构建通过，既有MSTEST0032提示保留。日志：`artifacts/view-only-verification.log`。

上游共享115、算法57、HALCON边界5项测试均在两框架通过，加上标签共792项；原生视图浏览验证见`../DP.Vision/VALIDATION.md`。不声明物理输入或相机吞吐已验收，未覆盖历史发布包。

## 历史：上游节点结果浏览器兼容回归

DP.Vision增加独立的WinForms/WPF多节点、多视图、图层选择浏览器及CanvasLayer显示名称。本轮未替换标签工作台布局、未改动算法、逐ROI阶段、报告或字库规则；依赖程序集已重新编译验证，历史包没有更新。

标签两框架各 **219通过**；完整真实OCR、物理外观、DB发现、OpenCV及两种原生工作台回归通过。52行Python/CTC一致52、人工精确51；375字符/370比较/5缺失/1超限及37个作者候选保持原证据语义。旧枚举测试MSTEST0032提示仍存在，不宣称清洁重建零警告。

日志：`artifacts/result-browser-verification.log`。上游共享118、算法57、HALCON边界5项测试也在两框架通过；新浏览器原生事件、像素及租约验证见`../DP.Vision/VALIDATION.md`。不将程序触发原生事件当作物理键鼠或真实相机吞吐验收。

## 历史：删除通用源裁剪入口

仅删除DP.Vision本轮新增的源Crop接口/实现/专用测试，不改标签算法、业务快照已有裁剪和持久化。两套解决方案构建通过，共享81、算法46、标签219项测试在两个框架均通过，共692项；原生通用画布和示例通过。本轮未重复模型相关回归，上次完整OCR结果见下节。

日志：`artifacts/remove-crop-build.log`、`artifacts/remove-crop-tests.log`及`../DP.Vision/artifacts/remove-crop-verification.log`。

## 历史：客户统一图像源

增加`IInspectionEngine`的源输入扩展入口，实际图/参考图在返回任务前保留，后台结束后统一释放；继续委托原有分阶段检测。WinForms/WPF工作台的SetActualImage/SetReferenceImage改为接收`IImageSource`，内部复制为不可变业务快照；持久化格式、原始证据和标签图像尺寸/布局限制不变。

两框架各 **219通过**，共享各94、算法各46，共718项；完整真实OCR、物理外观、DB候选发现和两种原生工作台通过。新增双输入保留、成功/失败/取消清理、保留实际源失败时释放参考源及位深拒绝回归。完整日志`artifacts/source-verification.log`及`../DP.Vision/artifacts/source-verification.log`。重新编译时旧枚举测试可能提示MSTEST0032；不将增量构建无警告误写为清洁重建保证。

源接口与工作台图像输入是破坏性变更，示例已迁移；旧发布包未重建。仍保留标签业务ImageFrame快照及低层InspectionRequest入口，不冒称全链零复制。见[统一源用法与边界](../DP.Vision/UNIFIED_IMAGE_SOURCE.md)。

## 历史：单类型文件、功能目录与枚举E前缀

源码、测试、工具和示例按功能分目录，独立类型拆成独立文件；私有辅助类型保留嵌套作用域，资源路径及命名空间不变。自定义枚举全部采用E前缀，不提供旧类型别名或转发。新增全部9个标签枚举的成员/数值及Flags回归；现有配方、报告、字库和原生UI流程仍通过。

双框架各 **213通过**，通用共享测试各75、算法各27通过；完整真实OCR、物理外观、DB候选发现及WinForms/WPF工作台回归通过，构建零警告/错误。日志`artifacts/structure-verification.log`；成员完整性日志`../DP.Vision/artifacts/structure-integrity.log`。入口见[功能目录](STRUCTURE.md)及[源码索引](SOURCE_INDEX.md)。本批使用最新源码，旧dist及历史包不覆盖，外部宿主需修改枚举类型引用并重新编译。

## 历史：通用ROI UI分层接入

通用ROI编辑类型与控件接口移至 `DP.Vision.UI`，标签Runtime、Core、Adapter及中立算法无UI引用；移除无人调用的 `VisionAdapter.ConvertRoi`，避免把UI类型带入后台。原生控件获得共享UI依赖，发布脚本检查该DLL。标签旧矩形手势和矩形检测范围仍是现状，不宣称已支持任意Region检测。

两框架各 **203通过**，完整真实OCR和WinForms/WPF通过，Release零警告/错误；日志 `artifacts/roi-ui-layer-verification.log`。通用共享测试各68、算法各24通过。四种发布组合在 `artifacts/roi-ui-layer-package` 独立运行通过，包含新UI程序集；日志 `artifacts/roi-ui-layer-package.log`、`artifacts/roi-ui-layer-published.log`。

## 历史：删除重复工程（破坏性工程变更）

- 已删除四个旧工程目录、csproj及解决方案项：`DP.LabelInspection.Barcode.Zxing`、`DP.LabelInspection.Ocr.Onnx`、`DP.LabelInspection.Vision.OpenCv`、`DP.LabelInspection.Vision.OnnxDetection`。
- 业务运行时统一到 `DP.LabelInspection.Runtime`；DB检测器实际算法归入 `DP.Vision.OnnxDetection`，不依赖标签工程。没有旧命名空间或程序集类型转发；所有样例、探针、测试和工程引用已更新。
- 清除旧bin/obj及构建服务器缓存后重建，锁定恢复通过；标签两框架各 **203通过**，Release零警告/错误，完整真实OCR及原生WinForms/WPF通过。新增程序集边界回归。
- 日志 `artifacts/project-consolidation-verification.log`；通用Vision完整验证 `../DP.Vision/artifacts/project-consolidation-verification.log`，core各60、算法各24通过。真实DB候选37、OCR/CTC52/52保持原口径，不是工业精度证明。
- 发布脚本要求新Runtime/检测程序集存在，并拒绝四个已删除DLL。`artifacts/project-consolidation-package` 的四种组合均从发布目录独立运行通过；日志 `artifacts/project-consolidation-package.log`、`artifacts/project-consolidation-published.log`。旧dist与历史验收包不修改，不代表当前结构。
- 已显式使用固定net48参考程序集包，关闭SDK按机器环境自动补充ReferenceAssemblies包，避免Shell/定向包环境造成锁文件图变化；两套完整verify和发布锁定恢复均通过。

## 历史：UI配置统一与冗余代码清理

- 源码WinForms/WPF均使用显式ROI数据/质量项目。移除WinForms重复的条码印刷开关；旧Enabled仅作兼容镜像，旧JSON有无Tasks及冲突值均有回归。
- 删除 `VisionAlgorithmImages.cs`，统一使用 `AlgorithmContractAdapter`；删除无人调用的旧Gray/Otsu和Rect辅助方法；删除废弃的一次性迁移脚本。
- CTC保留公开入口但删除重复解码算法体，委托 `DP.Vision.Algorithms.CtcDecoder`；原混合命名 `RegistrationAndBarcode.cs` 更名为仅定位职责的 `TranslationRegistration.cs`。
- `FieldBindingEvaluator`、`RequiredAppearancePolicy` 仍被旧第三方后端兼容通道调用，不冒充死代码删除。旧发布目录和历史证据也未删除。
- 两框架各 **201通过**，0失败/跳过；Release零警告/错误，完整真实OCR及原生WinForms/WPF验证通过。日志 `artifacts/config-cleanup-verification.log`。旧验收包是清理前快照，使用 `start.cmd` / `start-wpf.cmd` 构建运行最新源码。

## 历史：逐ROI流程及第二批算法接入

- net48/net8.0-windows 各197通过，0失败/跳过；两框架完整原生WinForms/WPF与真实OCR/字库工作流通过，Release零警告/错误。
- `artifacts/roi-workflow-verification.log` 是当前完整日志；以下旧批次数字为历史记录。
- 新增阶段调用顺序、当前ROI早停/后续继续、依赖缓存与循环、未完成空结果、项目JSON往返、整段文字质量替换和Analyze兼容入口测试。
- OCR52行原Python/CTC52/52、人工51/52；未变成工业准确率声明。新的作用域OK不再被无条件全图REVIEW覆盖。
- 四种发布组合已在 `artifacts/roi-package-final` 验收：net48/net8 × WinForms/WPF，迁移程序集齐全，均从发布目录独立运行smoke通过。日志 `artifacts/roi-package-check.log`、`artifacts/roi-published-smoke.log`；不含模型、生产图、配方或报告，也没有覆盖旧dist。
- 原生WPF项目编辑API回归和新增编辑栏截图已检查；这不是物理键鼠路由验收。
- 规则：[CURRENT_INSPECTION_FLOW.md](CURRENT_INSPECTION_FLOW.md)；迁移及边界：[ALGORITHM_MIGRATION.md](../DP.Vision/ALGORITHM_MIGRATION.md)。旧dist未覆盖。

## 历史批次

算法下沉第一批：单字比较、固定/空白已改调DP.Vision中立算法，QR检查器可独立注入；新增5项隔离/兼容测试，两框架各175通过，完整原生及真实OCR工作流通过，Release零警告/错误。最新完整日志 `artifacts/algorithm-migration-verification.log`；范围与未完成项见 [迁移记录](../DP.Vision/ALGORITHM_MIGRATION.md)。这是当时记录；当前内置后端已切换逐ROI编排。

ROI参数中文化：26项中文显示名/说明，中文布尔及枚举，独立底部说明区；两框架原生窗口验证选项帮助可见、中文值转换及保存容差0不修改其他设置。日志`artifacts/roi-chinese-verification.log`；操作说明[ROI_PARAMETER_GUIDE.md](ROI_PARAMETER_GUIDE.md)。

必检外观/ROI汇总：请求比对但未完整执行判NG，每ROI一个父项，完整子项保留；新增6项测试及原生ROI明细探针。当前完整日志`artifacts/required-appearance-verification.log`，策略见[ROI_EVIDENCE_POLICY.md](ROI_EVIDENCE_POLICY.md)。

整图参考模式修复：增加可见的“无整图参考（可用单字库）”及尺寸提示，保留ROI/字库绑定，不暗中删除模板。新增3项测试，包含自建16×20单字参考在64×32整图上无参考执行真实外观比较；WinForms探针验证旧参考尺寸冲突后显式切换。日志`artifacts/reference-mode-verification.log`。

多图补录/人工分割增量：13项会话测试；原生WinForms两框架验证已有B不阻塞新增C、无自动服务时补框/点击切线/拖边界/撤销重做、跨图暂存3字并原子发布。完整日志`artifacts/library-editor-verification-final.log`，截图`artifacts/winforms-net48.png.quick-library.png.multi-image.png`及net8对应文件。当前页面分离当前候选与跨图清单；换图只清候选，不清已暂存项。

粘连制库修复：另增6项测试，正式检测仍保守；真实同批10张样本5张原路径、4张需复核候选、1张仍拒绝，不以候选计数代替准确率。日志`artifacts/touching-verification.log`。

快速制库首批增量：9项Core/Storage/原生切割测试，以及两框架WinForms页面的真实OCR候选、人工文字重切、勾选4字原子发布和换图清空检查。日志`artifacts/quick-library-verification.log`，流程与限制见[QUICK_GLYPH_LIBRARY.md](QUICK_GLYPH_LIBRARY.md)。该首批日志仅说明当时行为，后续增量见上。

## 构建与运行

- Windows x64；.NET SDK 10.0.302构建。
- 实际执行net48与net8.0-windows两个目标，不只是跨框架编译。
- 全解决方案Release：**0警告、0错误**。
- 锁定还原：`dotnet restore --locked-mode`通过。

## 单元/集成测试

| 目标 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| net48 / x64 | 175 | 0 | 0 |
| net8.0-windows / x64 | 175 | 0 | 0 |

涵盖：图像所有权/裁剪、配方验证、ROI越界/重叠、质量降级、固定/空白缺陷、忽略保护带、取消/释放、架构依赖、CTC重复/blank、BGR/padding、单行声明、像素归属/邻字去除、点画、粘连/遮挡拒绝、归一比较、显式重复单元、实际ECC平移、实际Code128/QR解码及污损扫描候选、字库不可变历史/导入/来源/并发写入、字表原子导入、配方回读、报告精确字库快照、反馈与ZIP完整性。

代码：`tests/DP.LabelInspection.Tests/{InspectionTests,TextRecognitionTests,FullInspectionTests}.cs`。

## 私有生产数据回归

### OCR与字符身份

- 8张实拍复制图、48个选定ROI，另加4个合成乱序/重复组合ROI。
- 两套C#运行时均：52/52文本、52/52全时间步CTC类别与Python参考一致。
- 人工全文一致51/52：47/48实拍＋4/4合成。
- 已知例外保留：frame-4/part，OCR为`WO00001301`，人工为`W000001301`，未纠正。
- 输入SDK的只有图像、ROI和明确配置，人工转录/参考输出仅在执行后比较。

### 分割与单字比较

| 口径 | 数量 |
|---|---:|
| 当前启用外观检查的实拍分割字符 | 339 |
| 合成候选字符 | 36 |
| 实拍单字比较 | 334 |
| 合成单字比较 | 36 |
| 已绑字库但缺字实例 | 5 |
| 历史基线中未绑定字库的107个候选（frame-2/3/4） | 现不执行分割/比较 |
| 超阈值候选 | 1 |

外观检查现在可选：普通文字ROI未绑定字库时，不执行分割、不报缺库或字符未覆盖。当前生产回归合计375个分割字符、370次比较、5个缺模板实例；相比旧482字符/112未比较项，减少的是107个未请求的分割候选，不是消除缺陷。

常规与窄体字形按类别检索，来源顺序不参与检索。示例回归中的超阈值候选没有被当成已标注真缺陷，也没有通过调整阈值人为隐藏。

冻结`M012200015`回归：分割10字，已知邻字墨迹残留0；`Wafer`5字分割通过。人工构造的重叠投影案例验证保留自身笔画和小点噪声、只去除已知邻字墨迹。

### 无ROI探索

候选检测模型在私有留出图上的结果仅用于探索和配方准备；公开仓库不发布对应图片、具体生产标识或验收结论。

这个数量**不是37个已证实正确且完整的字段**。未验证自动覆盖精确率/召回率；不支持以水平矩形近似替代任意旋转/透视校正。

## 字段绑定增量验证

`FieldBindingTests.cs`覆盖文字/条码精确比较、大小写与O/0、低置信/低质量、零码/多码、任务周期错配、缺失/过期/未来数据、不可变快照、配方/报告/ZIP与双向原始读数约束。

WinForms实际ONNX识别A1020、ZXing解码QR后，与本次任务字段形成三个匹配约束；换图验证任务数据清空；文字和条码相互一致但外部任务错误时，分别报告不匹配。证据见`artifacts/binding-verification.log`及`binding-ui-*.png.txt`。

## 一维条码打印检查增量

新增逐条/逐空隙的局部缺墨、多墨检测，替代旧扫描差异；解码成功为OK信息，打印异常单独输出NG和原图坐标/面积。测试覆盖水平/垂直、断条、孔洞、空隙污点、附带文字隔离、ROI偏移、阈值、序列化、未解码仍检查、质量降级、关闭与二维未支持。

用户此前同一原图/ROI回放：两运行时都检出左21、右8个超阈值缺墨候选。未经完整人工标注，不能作为工业真缺陷计数/准确率。详细范围和局限见`BARCODE_PRINT.md`。

## QR模块打印检查增量

实际ZXing/OpenCV处理生成的QR：正常图、局部缺墨/多墨、90度旋转、原图面积/坐标、大版本、低分辨率、透视网格不可用时保守REVIEW。增量覆盖固定结构整模块翻转、版本1/7/32/40与镜像保护；当前未核验全部纠错前后模块差异，数据区完整模块翻转不能保证检出；无已完整标注的用户QR实物缺陷评估。见`QR_PRINT.md`。

## QR静区检查增量

新增独立`CheckQrQuietZone`开关（默认关闭，旧配方兼容），检查四周4模块留白的墨迹。覆盖原图污点框/面积、正常图、恰好4模块留白边界、静区外墨迹不误算、紧ROI提示、面积阈值及配置持久化。未据此宣称ISO评级；数据区整模块纠错差异和DataMatrix外观仍未实现。

## WinForms画布缩放/平移

原问题通过真实WM_MOUSEWHEEL消息复现：图像始终适应窗口，没有缩放状态。修复后双运行时UI探针验证滚轮放大实际栅格像素、鼠标锚点不漂移、中键平移、放大后的缺陷命中、原图ROI绘制/移动、Home复位、1:1及换图清除旧视图。图像与覆盖框、命中测试共用同一视图变换；不修改检测图像或配方像素。

日志`artifacts/zoom-*`；本次仅增强WinForms及其共享ImageViewerControl，不宣称WPF同步具有相同交互。

## 实物一维条码灰度漏检

原图实际ROI回放证明灰度70～120的受损墨迹被132的Otsu阈值吞掉。新增逐列深色参考的局部墨色损失，合并二值孔洞掩膜后按连通域输出；旧配方兼容。指定原图点(587,806)候选框覆盖由FAIL变PASS，并验证合成水平/垂直24像素损失、开关/阈值、均匀灰条与持久化。不是工业召回率，边缘/端部误报及整条共同损失仍待校准。见`BARCODE_INK_LOSS.md`。

## 条码证据层级与对比视图

新增每条码一个F汇总，保留`EvidenceGroups.Children`及全部原始Findings。测试NG/REVIEW聚合、编号、JSON往返、ZIP完整子项和总览去除小框；原生WinForms探针使用实际可解码CODE128断条检查主列表/画布汇总、原图/标记分离、子项完整、联动视图及报告不变。见`BARCODE_EVIDENCE_GROUPS.md`、`artifacts/barcode-groups-*`。这是呈现改进，不是精度改进。

## QR类型与读取失败策略

新增显式QR/一维ROI选择及持久化，QR无读数不走一维打印回退；实际执行后不可读NG（不因外观质量门禁转成REVIEW），未加载解码器/无ROI探索仍REVIEW。验证错误码制约束独立于打印开关。用户严重污损QR原始报告图像/ROI回放汇总NG，局部网格未知保留子项REVIEW。缺陷页移除重复原图，仅保留可缩放标记图与完整明细，UI探针验证新QR选项。日志`artifacts/qr-policy-*`与`user-unreadable-qr-*`。

## 独立DP.Vision兼容验证

通用图像/ROI/Region/XLD与新原生控件已独立到`../DP.Vision`、`DP.Vision.Winform`、`DP.Vision.WPF`。本SDK保留旧公共类型，但WinForms主图/条码详情/字库的内部绘制已经切至DP.Vision；原生WPF主图也已替换。Adapter的5项测试覆盖空对象、游程、亚像素原点偏移、图像所有权及标签只读图层转换。原矩形ROI业务事件保留，详见[VISION_WORKBENCH_MIGRATION.md](VISION_WORKBENCH_MIGRATION.md)。既有性能矩阵不是本轮完整工作台吞吐实测。

## 原生/中立画布同机性能对比

独立可选`CanvasBenchmark`在net48/x64实测HALCON23.11 HWindowControl与当前GDI+ ImageViewerControl。8场景×2控件×2轮＝32进程，通过数据数量检查和DwmFlush返回检查。约4MP缓存重绘0.62/7.23ms，10万XLD点11.46/33.34ms，10万碎片Region游程16.80/14.02ms（原生/通用）。连续换图CPU/内存与GC差异明显。完整口径、P95与限制见`CANVAS_BENCHMARK.md`；绘制提交时间不是屏幕FPS，不代表未来GPU画布上限。未修改生产渲染代码，未测长期耐久性。

## HALCON几何可行性探针

本机HALCON23.11实际提取3个Region（227条游程）、3条XLD contour（228点）；从中立数据重建Region逐对象XOR面积0，亚像素点逐值相等，提取真实边缘属性。释放源对象后输出JSON，当前WinForms画布在net48/net8独立显示、缩放/平移，孔洞/分离岛及轮廓像素检查通过，显示进程未加载HALCON模块。普通单元测试另覆盖中立几何所有权、JSON、半开区间、非法坐标。探针不加入默认构建；未声称完整HALCON适配器或全部XLD语义、性能已完成。见`HALCON_CANVAS_FEASIBILITY.md`。

## 模型身份

```text
recognition SHA256
48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b

detection SHA256
d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9
```

识别器另验证模型哈希不符拒绝、取消及释放后调用拒绝。简单零张量兼容性探针继续保留，但不替代真实图回归。

## 桌面实运行

两套WinForms实际窗口执行：
- Windows鼠标消息框选，坐标转换误差≤1像素。
- 合成缺墨/污点NG与真实OCR `A1020`。
- 乱序重复组合36次比较，显示绿色实际字符边界及单字/参考/差异图。
- 独立字库控件实际载入、绘制、鼠标裁剪。
- 后台报告保存、人工反馈、ZIP导出；测试数据隔离在`artifacts/desktop-smoke-data`。

两套WPF实际窗口执行：原生Canvas图像显示、异步调用同一SDK并产生400原始平方像素污点NG、完成关闭与截图。WPF鼠标接口已实现，但没有把WinForms的鼠标消息自动测试冒充WPF鼠标实测，也没有声称编辑器UI同等覆盖。

证据：
- `artifacts/full-verification.log`
- `artifacts/ocr-net48.log`、`ocr-net8.log`
- `artifacts/winforms-net48.png`、`winforms-net8.png`
- 同名追加`.production.png`、`.glyphs.png`、`.library.png`与`.txt`
- `artifacts/wpf-net48.png`、`wpf-net8.png`与`.txt`

## 不应外推的结论

没有标注真实缺陷集，没有工业误检/漏检指标，没有盲测或独立合成数据泛化证明。部分图是建库来源或近重复图；合成字符来自参考字形。以上验证证明实现可执行、关键行为保留及已知回归没有重现，不证明所有印刷缺陷都能检出。

不是HALCON、Visual Studio设计器、全DPI、所有宿主原生版本冲突或热卸载测试。没有修改原始生产图或将其发布到外部。

完整复现命令与交付边界见`FULL_DELIVERY.md`。
