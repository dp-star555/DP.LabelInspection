# DP.LabelInspection

**0.2.0-preview.1 · Windows x64 · .NET Framework 4.8 / .NET 8 · C# SDK与桌面控件**

已将基础检测、真实OCR、单字分割/字库比较、条码、配方与报告持久化整合为可运行工作台。没有调用Python解释器或HTTP服务；旁边的Python项目与生产原图保持不变。

> 这是完整操作链路的工程预览版，不是工业准确率认证。必需读取/质量未完成明确NG；不能把识别成功当成外观合格。HALCON适配器未实现，不计作已支持的运行后端。

内置后端已切换逐ROI前检→读取/引导比较→独立印刷质量，当前ROI失败继续其他ROI；项目设置和阶段轨迹已接入持久化及原生UI。物理分割/配对/单字及整段质量、读码/1D/QR、ONNX识别已接入DP.Vision中立接口，旧算法工程及其程序集已删除，不保留旧命名空间或类型转发。见[当前流程与兼容边界](CURRENT_INSPECTION_FLOW.md)及[迁移验证记录](../DP.Vision/ALGORITHM_MIGRATION.md)。

## 标签检测节点封装准备

目标是把当前完整检测能力封装为一个可复用的业务节点，而不是重新拆分OCR、条码和空白算子。已完成源码盘点、节点契约建议、Demo宿主功能拆分及封装验收清单，见[节点封装资料](docs/node-packaging/README.md)。这些是后续实施依据，当前尚未交付标签节点插件；正常节点运行将直接调用SDK，配置时复用工作台，Vision仍只管理视图与图层。

## 直接运行

在本目录双击或执行：

```powershell
.\start-ocr.cmd        # 推荐：完整WinForms工作台，默认net8
.\start-ocr.cmd net48  # 同样功能，实际运行CLR4
.\start.cmd            # 工作台自动查找本地模型；也可在界面加载OCR模型
.\start-wpf.cmd        # 原生WPF控件示例
.\start-wpf.cmd net48
```

直接运行工作台EXE或从Visual Studio启动，也会查找程序/工程目录的`models/rec.onnx`及当前工作区原型已有的识别模型。顶部明确显示OCR加载状态；找不到模型时，字符页显示原因，不再空白。显式配置的无效模型仍报错，不静默回退。

`start-ocr.cmd`优先使用`DP_LABEL_REC_MODEL`，否则找`models/rec.onnx`，再尝试当前工作区原型发行包已有的PP-OCRv4文件。只是读取模型文件，不启动Python。

- `DP_LABEL_REC_MODEL`：可信的PP-OCRv4识别模型路径。
- `DP_LABEL_DET_MODEL`：可选检测模型路径；未设置时从识别模型同目录寻找`ch_PP-OCRv4_det_infer.onnx`。
- `DP_LABEL_DATA`：本地字库、报告和历史根目录，默认`%LOCALAPPDATA%\DP.LabelInspection`。
- `DP_LABEL_SAMPLES`：可选私有生产样例目录；当前工作区可自动找到原型样例。

## 已实现的功能

| 功能 | 实际行为 |
|---|---|
| 有参考 / 无参考 | 固定Key与可变Value独立配置；无参考不整体放行 |
| 图像质量 | 不强制整图清晰度/对比度门控，不降级已测得的印刷缺陷 |
| 固定区域 | 原始像素缺墨、多墨、连通域面积、容差、忽略遮罩 |
| 平移配准 | 在固定内容上估计受限ECC平移，最大12px；无纹理/失败/越界不强行比较 |
| 空白区域 | 污点检测，保留原始像素位置和面积 |
| 单行OCR | 真正BGR预处理、动态宽度、ONNX推理、CTC blank/重复处理及模型哈希 |
| 候选发现 | DP.Vision.OnnxDetection提供实际DB候选检测；须显式用于配方准备，没有配置ROI的验收请求为NG |
| 字符分割 | 测量空隙/连通域归属；保留点画、小噪点；拒绝强切粘连、边缘裁切与彩色遮挡 |
| 单字参考比较 | 可选：文字ROI绑定类别＋固定版本才启用分割/字形比较；未绑定时仅做OCR/内容规则，不报缺字库或外观未覆盖。支持任意重排和重复字符；显式等宽单元仍要求字库 |
| 字库管理 | 新建、单字图像/裁剪、确认保存、规则字表导入、单字删除、归档/恢复、历史读取、JSON导入/导出；[多图制库](QUICK_GLYPH_LIBRARY.md)：多图补录、手工补框/切开粘连/改边界、撤销重做、跨图清单、缺字筛选和原子新版本 |
| 版本安全 | 不可变修订、过期/并发编辑拒绝；导入创建新ID，不静默重绑定 |
| 两类示例字库 | 常规与窄体字形可按类别共用；仍是候选良品参考，不随仓库分发私有字库 |
| 字段绑定 | 文字/条码ROI交叉校验、每次请求的外部任务字段；校验周期、有效期、唯一读数；详见[DATA_BINDING.md](DATA_BINDING.md) |
| 内容规则 | 明确预期、全匹配正则、字符集、长度；不从OCR生成预期。配置Expected时明确显示内容匹配/不匹配；实际读取可靠时匹配为内容OK、不符为NG；必需读取不可靠时NG并阻断后续质量。外观独立判定 |
| 等宽单元 | 仅用户明确给出等宽布局/预期时使用，不用于强行切割随机Value |
| 条码 | ZXing真实1D/QR等解码＋独立一维条/空隙缺墨、多墨检测；原图缺陷框、面积、局部阈值NG；另支持QR模块内部缺墨/多墨（[QR_PRINT.md](QR_PRINT.md)）；非ISO评级，DataMatrix外观未实现，见[BARCODE_PRINT.md](BARCODE_PRINT.md) |
| WinForms | 一维/QR独立ROI选项、条码F汇总＋单一缺陷标记视图＋完整子项（[说明](BARCODE_EVIDENCE_GROUPS.md)）、滚轮缩放/中右键平移/1:1/适应窗口、图像/鼠标ROI、坐标与规则编辑、库版本绑定、阈值、单字/参考/差异图、补库与下载 |
| 桌面工作台 | 模型加载、样例加载、配方读写、逐图批量、自动报告历史、人工复核、ZIP导出 |
| 中立几何验证 | Region游程/XLD contour亚像素折线可在当前画布显示；HALCON23.11实测提取，显示侧无HALCON依赖。验证底座与限制见[HALCON_CANVAS_FEASIBILITY.md](HALCON_CANVAS_FEASIBILITY.md)，不等于完整通用画布交付；新通用能力已独立至[DP.Vision](../DP.Vision/README.md)，旧工作台的WinForms/WPF图像和证据绘制现已接入DP.Vision，公开类型和矩形ROI事件保留，见[工作台迁移记录](VISION_WORKBENCH_MIGRATION.md)；[历史同机性能实测](CANVAS_BENCHMARK.md)保留独立旧版基准，不是本轮工作台FPS结论 |
| WPF | 独立原生图像/ROI/异步检测/证据/字符图控件，无WinForms嵌套；编辑器UI尚不与WinForms全等 |
| SDK | 无界面同步/异步调用，宿主选择后端/存储/识别器；显式资源所有权 |

## WinForms使用流程

1. 设置 `DP_LABEL_SAMPLES` 后，可用“加载生产样例”选择宿主提供的测试集；公开仓库不附带生产图片、配方或报告。
2. 自己的图片：载入待检图，按固定/单行文字/条码/空白/忽略框ROI。检测后鼠标放在缺陷处滚轮可围绕该点放大；中键或右键拖动平移；工具栏“1:1”显示原始像素，“适应窗口”或Home复位。左键仍用于证据选择/ROI，坐标始终是原图像素；缩放平移不改检测结果，换图恢复适应窗口。
3. 勾选“选中/调整ROI”，点击框内可拖动，选中后拖动八个控制点可缩放；Esc取消当前拖动，Shift拖动可另建ROI。修改保留规则/字库绑定，清除旧结果，需要重新检测。“编辑ROI/规则”仍可精确输入坐标、尺寸、类型、预期和正则，选择类别并**显式绑定其修订**。
4. 单字库可载入图像后鼠标裁剪、确认单字标签；也可点击“多图制库”：自动候选不足时手工补框、点切线分开粘连、拖边界；核对后加入跨图清单，继续导入下一张，随时保存部分字符为新版本。默认跳过已有字，不因一个重名阻止新增。当前只支持ASCII字母/数字，每字符每类别一个参考，图块4–512像素。更新字库不会自动修改旧配方绑定。详见[多图补录与人工分割说明](QUICK_GLYPH_LIBRARY.md)。ROI参数已中文化，选中后底部显示用途、单位、范围和调节影响，见[参数说明](ROI_PARAMETER_GUIDE.md)。
5. 正式检测必须配置ROI。候选检测器可由宿主显式调用用于配方准备，不以空ROI验收请求代替发现操作。
6. “字段绑定”可关联其他文字/条码ROI，或绑定TaskData字段；“本次任务数据”提供人工调试输入，正式后台数据通过SDK注入。换图清空旧数据，绑定随配方保存。
7. 检测后看“单字 / 参考 / 差异”标签页；点击F证据行或图中F区域，只显示关联单字。字符级证据定位单字，行级证据显示该行；“显示全部单字”恢复画廊。可下载实际单字块或确认补库。
8. 检测报告自动后台保存；“历史/人工复核”追加人工结论，“导出当前ZIP”导出完整证据。
9. “逐图批量”依次使用当前ROI/配方；尺寸错误单独报告，不伪造通过。取消停止当前批次。

参考图模式需先载入同尺寸参考。“上游已对齐”只是调用方声明；未勾选时使用固定ROI估计平移。不能使用随机Value与前一张标签做固定像素差分。

## 程序集与依赖边界

```text
DP.LabelInspection.Contracts         netstandard2.0，自有图像/ROI/结果/任务接口
DP.LabelInspection.Core              netstandard2.0，校验、调度、质量与覆盖判定
DP.LabelInspection.Storage           netstandard2.0，JSON/PNG/ZIP与不可变字库
DP.LabelInspection.Adapter.Vision   netstandard2.0，业务图像/证据与中立图像/几何转换
DP.LabelInspection.Runtime          net48 / net8，唯一标签运行时装配工程
DP.Vision.Algorithms                netstandard2.0，中立视觉任务契约
DP.Vision.OpenCv                    net48 / net8，通用分割与印刷质量实现
DP.Vision.Onnx                      net48 / net8，独立OCR模型推理
DP.Vision.Zxing                     netstandard2.0，实际读码
DP.Vision.OnnxDetection             net48 / net8，实际DB检测（ONNX＋OpenCV），无标签依赖
DP.LabelInspection                  net48 / net8，WinForms主控件与中立画布适配
DP.LabelInspection.Wpf              net48 / net8，原生WPF控件与中立画布适配
```

主控件为`DP.LabelInspection.LabelInspectionControl`；字库控件为`DP.LabelInspection.GlyphLibraryControl`；WPF为`DP.LabelInspection.Wpf.LabelInspectionControl`。

Core与控件没有Mat/HObject/ONNX类型。具体视觉适配器可以依赖其实现库，但不把这些类型泄露到核心契约。标签SDK当前提供普通SDK/控件注入入口，尚未实现标签检测的工作流节点适配；现有DP.WorkFlow节点、能力注册和编辑页扩展机制可供外层封装使用，不应反向成为标签Core或Vision的依赖。

## 最小SDK组合

客户图像入口统一为`DP.Vision.IImageSource`。两个工作台的`SetActualImage/SetReferenceImage`也接受源；内部业务快照和持久化格式仍保留，转换会复制。见[统一图像源、所有权和迁移说明](../DP.Vision/UNIFIED_IMAGE_SOURCE.md)。

```csharp
using DP.Vision;
using DP.LabelInspection.Adapter.Vision;
// Runtime替代已删除的四个按厂商命名的标签程序集。
// using DP.LabelInspection.Runtime;
// using DP.LabelInspection.Runtime.Recognition;
// using DP.LabelInspection.Runtime.Codes;
var codec = new OpenCvImageCodec();
var store = new InspectionStore(dataDirectory, codec);
using var recognizer = new OnnxTextLineRecognizer(modelPath,
    new OpenCvTextLinePreprocessor(), expectedSha256: modelHash);
using var backend = new OpenCvInspectionBackend(recognizer,
    libraries: store, barcode: new ZxingBarcodeDecoder());
using var engine = new InspectionEngine(backend);

using var source = VisionImage.CopyFrom(info, pixels);
var region = new InspectionRegion("value", ERegionKind.Text, bounds,
    singleLine: true, field: new FieldSettings("production-regular", 1));
var recipe = new InspectionRecipe("label", source.Info.Width, source.Info.Height,
    EInspectionMode.Free, EAlignmentMode.AssumeAligned, new[] { region });
var report = await engine.InspectAsync(source, recipe, cancellationToken: token);

// 如需持久化，保存独立业务快照；此处明确发生复制，不引入额外客户租约。
var snapshot = AlgorithmContractAdapter.ToLabel(source);
var request = new InspectionRequest(snapshot, recipe);
var jobId = store.SaveReport(request, report, codec.Annotate(snapshot, report));
store.ExportReport(jobId, newZipPath);
```

需引用上述各项目对应命名空间。宿主可将同一个`IInspectionEngine`注入WinForms/WPF，也可只用SDK。引擎对自身后端串行执行，界面不拥有注入引擎。关闭前先取消并异步等待，再由宿主释放引擎与模型；不要在UI线程用`.Wait()`。

标签入口接受统一图像源，内部业务快照仍限制为Gray8/Bgr24、≤12000边长、≤1600万像素；其他布局显式拒绝。矩形为原图坐标半开区间。字符比较消费拥有的Patch，**不能重新裁相交外接框**。取消不能硬中断正在执行的原生算子。

## 验证与交付

详细验证、统计口径与限制见 [VALIDATION.md](VALIDATION.md)，交付边界见 [FULL_DELIVERY.md](FULL_DELIVERY.md)。

```powershell
.\verify.ps1 -RecognitionModel "$env:DP_LABEL_REC_MODEL" `
  -ProductionAssets "$env:DP_LABEL_SAMPLES" -OcrOracle "artifacts\ocr-oracle.tsv"

.\package.ps1   # 不带模型/生产图发布两个框架的WinForms和WPF宿主及SDK依赖
```

开发基线首次生成方法见`FULL_DELIVERY.md`。模型仅在显式传`-ModelDirectory`时打包，生产图片和用户数据默认不打包。

主要依赖固定为OpenCvSharp4/runtime.win 4.10.0.20240616、ONNX Runtime 1.28.0、ZXing.Net 0.16.11、Newtonsoft.Json 13.0.3；依赖锁文件随项目保存。部署仍需对应.NET运行时、原生依赖、VC++运行库检查与许可审查，不能只复制一个主DLL。

## 不应误解为已经具备

- 工业缺陷率/漏检率认证、训练好的通用印刷缺陷网络。
- 不知道预期信息时保证整字/整行完全没漏。
- 任意角度、尺度、透视的全标签配准；当前仅水平候选与受限平移。
- ISO条码等级、自动猜测条码与邻近文字的关联（现在支持显式ROI绑定）。
- HALCON适配器/许可、相机/PLC/MES、插件原生库隔离与可靠热卸载。
- WPF与WinForms所有编辑界面功能完全一致。

原型阶段的接口可能继续演进，不承诺预览版二进制兼容。真实样例、报告及模板来源含生产标识，不应未经审查对外发布。

## 功能目录与类型命名

已拆分独立类型、按功能建立目录，并将自定义枚举统一为E前缀。命名空间和资源键保持不变，私有嵌套辅助类通过独立partial文件保留作用域。外部宿主需更新枚举类型引用并重新编译，不保留旧类型别名。

详见[功能目录](STRUCTURE.md)与[源码类型索引](SOURCE_INDEX.md)。

## 代码排版与中文参数说明

两个解决方案共用4空格、代码块换行与CSharpier排版规则；源码、测试、工具及示例中的说明性注释已全量中文化，源码公开方法参数文档已补齐。坐标单位、资源所有权、固定版本和取消语义均有说明。范围、开发命令与验证记录见[代码可读性说明](../DP.Vision/READABILITY.md)。
