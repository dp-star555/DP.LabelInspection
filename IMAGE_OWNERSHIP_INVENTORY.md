# 图像所有权清单与 `InspectionRequest.Actual/Reference` 调用链（第 0～1 步）

> 本文件是**只读分析交付物**，不修改任何生产代码。基线固定于 2026-09-29 的工作区状态。

## 0. 本轮基线

- HEAD = `9509980`（Merge PR #14），`main` 与 `origin/main` 同点。
- 工作区有 **7 个未提交文件**（全部属异常检测迁移的收尾）：

| 文件 | 变更 |
|---|---|
| `src/DP.LabelInspection.Runtime/Anomaly/CharacterAnomalyDetector.cs` | **−28 行**：删除 `Inspect(PixelSnapshot image, …)` 旧入口 |
| `src/DP.LabelInspection.Runtime/Anomaly/RegionAnomalyDetector.cs` | **−20 行**：删除 `Inspect(PixelSnapshot image, …)` 旧入口 |
| `src/…/OpenCvInspectionBackend/RoiSession.Anomaly.cs` | 调用点改为 `VisionSource?.Retain() ?? Bridge.ToVision(Actual)` |
| `tools/…/AnomalyBenchmark/Runner/BenchmarkExporter.cs` | 调用点显式 `AlgorithmContractAdapter.ToVision(image.Frame)` |
| `samples/…/Demo.WinForms/Runner/AnomalyDemo.cs` | 同上 |
| `tests/…/Anomaly/CharacterAnomalyTests.cs`、`RegionAnomalyTests.cs` | 同上（注释由"旧快照"改为"持久化像素快照"） |

### 0.1 结论：**这两个旧入口的删除应当保留**

理由（三条，均可复核）：

1. **它们只是适配器，不是算法入口。** 被删的两个重载体都只有两行有效逻辑：
   `using var source = Bridge.ToVision(image); return Inspect(source, …);`
   —— 复制发生在检测器内部，调用方看不见；删掉后复制点全部上移到调用方，符合"复制位置集中在实际需要延长生命周期的地方"。
2. **没有悬空调用者。** 全仓已无对这两个重载的引用（详见 0.2 的构建证据）。
3. **保留它们反而有害**：Runtime 层保留"吃标签快照"的入口，等于给后续所有 ROI/质量路径留了一条隐式整帧复制的后门；这正是本轮要收敛的东西。

### 0.2 构建状态（重要：全量构建的失败**不是编译错误**）

- `dotnet build DP.LabelInspection.sln -c Debug` → **44 个错误、222 个警告**，但：
  - 44 个错误**全部是 `MSB3027` / `MSB3021`**，且**全部落在 `samples/DP.LabelInspection.Demo.WinForms`**；
  - **编译器错误（`CSxxxx`）计数 = 0**；
  - 报错原文指明原因：`文件被"DP.LabelInspection.Demo.WinForms (55012)"锁定` / `被"Microsoft Visual Studio (12212)"锁定`
    —— **示例程序正在运行、Visual Studio 正开着**，占用了 `samples/…/bin/Debug/net48/` 下的 DLL，拷贝步骤失败。
- 单独构建（绕开被占用的输出）：
  - `src/DP.LabelInspection.Runtime`（含未提交改动）→ **0 警告 0 错误**
  - `tests/DP.LabelInspection.Tests`（Runtime/Adapter/Storage/Core 全链路）→ **0 警告 0 错误**
  - `samples/…/Demo.WinForms -f net48 -o artifacts/step0-sample-out` → **0 错误**

> 也就是说：**当前工作区是能编译的**；要跑全量构建，先关掉正在运行的示例程序与 Visual Studio。

---

## 1. 两类像素对象的所有权模型

| 类型 | 谁拥有像素 | 生命周期 | 释放方式 | 复制语义 |
|---|---|---|---|---|
| `DP.LabelInspection.Contracts.PixelSnapshot` | 自己（构造即 `Clone` byte[]，`PixelSnapshot.cs:43`） | GC | 无 `Dispose` | 构造/`Crop`/`CopyPixels` 都返回独立副本 |
| `DP.Vision.ImageFrame` / `IImageSource` | 租约持有者 | 显式 | `Dispose()`；借用者用 `Retain()` | 零复制借用 |
| 转换原语（`Bridge` = `AlgorithmContractAdapter`） | 转换结果归调用方 | 调用方决定 | 见下 | **一律复制**，注释已声明"不宣称零复制" |

**判断规则（本清单用）：** 只要一处代码让"运行中图像"活过了宿主给定的租约边界，它就必须是显式复制，并且复制点要出现在**真正需要延长生命周期的那一行**，而不是被请求对象惰性触发。

---

## 2. 引用表（按所有权分类）

### A. 运行时检测输入（租约，正确形态）

| 位置 | 图像从哪来 | 谁持有像素 | 持有多久 | 需持久化 |
|---|---|---|---|---|
| `Contracts/Inspection/Configuration/InspectionRequest.cs:70-88` | 宿主传入的 `V.ImageFrame` | 请求自己 `Retain()` | 到请求 `Dispose` | 否 |
| `Workbench/LabelInspectionControl.cs:620,644`（WinForms） | UI 载入的标签快照 → `new V.ImageFrame(guid, image)` | 控件 `_visionActual/_visionReference` | 到换图/关闭 | 否 |
| `Wpf/Workbench/LabelInspectionControl.cs:282,307` | 同上 | 同上 | 到换图/关闭 | 否 |
| `Adapter.Vision/Algorithms/InspectionSourceExtensions.cs:44-71` | 宿主借用的 `IImageSource` | 内部 `Retain` + `ImageProcessing.RunAsync` 租约 | 任务结束 | 否，但**先复制成标签快照**（见 4.3） |

### B. 请求内的惰性快照（**本轮要收敛的对象**）

| 位置 | 触发条件 | 复制量 | 备注 |
|---|---|---|---|
| `InspectionRequest.cs:85` → `_convertedActual` | 首次读 `Actual` | 整帧 | `Lazy<PixelSnapshot>` |
| `InspectionRequest.cs:87` → `_convertedReference` | 首次读 `Reference` | 整帧 | 仅当有参考图 |
| `InspectionRequest.cs:117-128` `ToSnapshot` | 上述两者 | `new byte[ByteLength]` + `CopyTo` | 不校验布局/尺寸（构造时已 `CheckLayout`） |

**不触发复制**的成员（已正确）：`ImageWidth`、`ImageHeight`（:131-150）、`HasReference`、`ReferenceWidth`、`ReferenceHeight`（:162-168）。

### C. 持久化 / 长期资产（**复制是应该的**，但复制点应显式）

| 位置 | 内容 | 复制时机 | 格式约定 |
|---|---|---|---|
| `Storage/Persistence/InspectionStore.cs:450` | `actual.png` | `SaveReport` | PNG |
| `Storage/Persistence/InspectionStore.cs:451-453` | `reference.png` | `SaveReport` | PNG |
| `Storage/Persistence/InspectionStore.cs:207 PutGlyph` / `:299 PutSheet` | 字库条目 / 字库表 | 写入时 | PNG |
| `Storage/Persistence/Internal/InspectionStore/FrameConverter.cs:25-51` | JSON 内嵌图像 | 序列化/反序列化 | **`png_base64`，不得改动** |
| `Storage/Persistence/SampleExporter.cs:109` | 训练样本裁图 | 导出时 | 目录 PNG |
| `Contracts/Libraries/GlyphReference.cs:44` | 字库参考图 | 载入/新增 | 带 `sha256` |
| `Contracts/Libraries/Anomaly/RegionAnomalySample.cs:28`、`CharacterAnomalySample.cs:53` | 异常训练样本 | 采集/训练 | 资产 |
| `Contracts/Libraries/Candidates/GlyphImportItem.cs:34`、`Core/Libraries/Drafting/GlyphDraftCandidate.cs:40` | 候选 / 草稿图块 | 制作过程 | 资产 |
| `Core/Libraries/Anomaly/AnomalyTrainingImage.cs:17` | 训练输入图 | 加入会话 | 资产 |

### D. 报告证据（图像是结论的一部分）

| 位置 | 内容 |
|---|---|
| `Contracts/Inspection/Results/RegionAnomalyEvidence.cs:74` | 整 ROI 热力图 |
| `Runtime/Anomaly/RegionAnomalyResult.cs:57`、`CharacterAnomalyResult.cs:33` | 热力图 |
| `Contracts/Text/Segmentation/CharacterPatch.cs:54` | 字符图块（物理证据，由 `AlgorithmContractAdapter.cs:65-80` 复制） |
| `Contracts/Text/Quality/GlyphComparison.cs:52,55,58` | actual / reference / delta 三图 |
| `Runtime/Anomaly/CharacterAnomalyCell.cs:37` | 训练单元格图 |

### E. UI / 预览 / 工具（为"看"而持有）

| 位置 | 用途 |
|---|---|
| `Workbench/LabelInspectionControl.cs:88-91,1432` | 工作台快照 + 预览卡片 |
| `Libraries/AnomalyLibraryControl.cs:50,65,359-381,425` | 良品图列表、平移副本（`Translate` 生成新快照） |
| `Libraries/AnomalyBatchTrainingControl.cs:53,366`、`Drafting/GlyphQuickBuilderControl.cs:100,560-573`、`GlyphLibraryControl.cs:39,287`、`Canvas/ImageViewerControl.cs:348` | 训练 / 草稿 / 字库 / 查看器 |
| `Codes/BarcodeComparisonControl.cs:13` | 条码对比显示 |
| `Imaging/DrawingImageConverter.cs:15,76` | `Bitmap` ↔ `PixelSnapshot` |
| `Adapter.Vision/Display/VisionAdapter.cs:14` | 标签快照 → 通用源（显示用） |
| `Runtime/Imaging/CvImages.cs:10,30`、`OpenCvImageCodec.cs:12,62,71` | Mat / PNG / 标注图 |
| `samples/…/UiInteractionProbe.cs:650,1145,1482`、`Runner/Program.cs:194` | 样例与 UI 探针 |

### F. 训练

| 位置 | 说明 |
|---|---|
| `Contracts/Libraries/Anomaly/IAnomalyModelTrainer.cs:15` | `Train(IReadOnlyList<PixelSnapshot> good, …)` —— 训练输入是资产，不是租约 |
| `Runtime/Anomaly/RegionAnomalyDetector.cs:58,145,182,300` | 训练入口；`:300 Crop(PixelSnapshot, …)` 仍被 `Train` 使用（非孤立） |
| `Runtime/Anomaly/CharacterAnomalyDetector.cs:219-258` | `VisionLines`：**同一标签图只转一次**、缓存于 `Dictionary<PixelSnapshot, IImageSource>`、`Dispose()` 统一释放 —— **已符合作用域所有权，可作为其他路径的样板** |
| `Core/Libraries/Anomaly/AnomalyTrainingSession.cs:49,704-706` | 会话持图；对齐时新建 `V.ImageFrame` |
| `Core/Libraries/Drafting/GlyphDraftSession.cs:13,24,44` | 草稿会话持源图 |

### G. 转换原语（复制真正发生的地方）

| 位置 | 方向 | 复制 |
|---|---|---|
| `Adapter.Vision/Algorithms/AlgorithmContractAdapter.cs:14-24 ToVision` | 标签快照 → Vision 租约 | 是 |
| `AlgorithmContractAdapter.cs:28-52 ToLabel` | Vision 借用源 → 标签快照 | 是（含尺寸/布局校验） |
| `AlgorithmContractAdapter.cs:65-80 ToLabel(CharacterSegmentation)` | 物理证据 | 是（字符图块） |
| `InspectionRequest.cs:117-128 ToSnapshot` | Vision → 标签 | 是（**惰性**） |
| `Adapter.Vision/Display/VisionAdapter.cs:14 CopyImage` | 标签 → 通用源 | 是 |

> 全仓 `Bridge.ToVision/ToLabel` 在 `src` 下共 **35 处**，集中在 `RoiSession.Quality.cs`（15）、`RoiSession.Anomaly.cs`（4）、`RegionAnomalyDetector.cs`（4）、`CharacterAnomalyDetector.cs`（4）、`RoiSession.cs`（3）、`RoiSession.Location.cs`（2）、`AnomalyTrainingSession.cs`（2）、`OpenCvInspectionBackend.cs`（1）。其中**只有 11 处**是在读请求快照（见 3.2），其余是证据/观测类型转换。

---

## 3. `InspectionRequest.Actual / Reference` 真实调用链

### 3.1 写入方（谁让快照存在）

| 入口 | 产生的快照 | 生产调用者 |
|---|---|---|
| 旧构造器 `InspectionRequest(PixelSnapshot actual, …)`（`:27-52`） | `_actualSnapshot` / `_referenceSnapshot`（**直接持有，不复制**） | ① `Adapter.Vision/Algorithms/InspectionSourceExtensions.cs:64` ② `tools/…/BarcodeRegression/Runner/Program.cs:47` ③ `tools/…/CompatibilityProbe/Runner/Program.cs:42` ④ 测试若干 |
| `FromVision(…)`（`:97-103`） | 只 `Retain`；快照为 `Lazy` | WinForms `Workbench/LabelInspectionControl.cs:825`、WPF `Workbench/LabelInspectionControl.cs:443`、OCR/Barcode 回归工具、测试 |

### 3.2 读取方（谁触发整帧复制）

**第 1 类：运行时检测 —— 11 处，全部是 `??` 兜底，Vision 请求不触发**

```
RoiSession.cs:218,228                 _request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)
RoiSession.Location.cs:34             _request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)
RoiSession.Location.cs:36             _request.VisionReference?.Retain() ?? Bridge.ToVision(_request.Reference!)
RoiSession.Quality.cs:35,200          _request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)
RoiSession.Quality.cs:55              _request.VisionSource?.Crop(…) ?? Bridge.ToVision(_request.Actual.Crop(r.Bounds))
RoiSession.Quality.cs:96              _request.VisionReference?.Crop(…) ?? Bridge.ToVision(_request.Reference!.Crop(original.Bounds))
RoiSession.Anomaly.cs:267,300,418     _request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)
```

计数核对：`Bridge.ToVision(_request.Actual` = **9 处**；`_request.Reference` = **5 处**，其中 **只有 2 处**（`Location.cs:36`、`Quality.cs:96`）会复制，另 3 处（`RoiSession.cs:121,122,136`）是 `ReferenceWidth/Height` **元数据**读取，不复制。

**第 2 类：持久化 —— 1 处，无条件读取，Vision 请求会复制整帧**

| 位置 | 行为 | 生产调用者 |
|---|---|---|
| `Storage/Persistence/InspectionStore.cs:450,451-453` | `_codec.EncodePng(request.Actual)` / `request.Reference` | `samples/…/Demo.WinForms/Runner/Program.cs:194`、`tools/…/BarcodeRegression/Runner/Program.cs:50`；测试多处 |

**第 3 类：UI / 预览 / 工具 —— 无条件读取（属第 3 步范围）**

| 位置 | 行为 |
|---|---|
| `samples/…/UiInteractionProbe.cs:650,1145,1482` | 取原图做位图/校验 |
| `samples/…/Demo.WinForms/Runner/Program.cs:194` | `codec.Annotate(request.Actual, report)` 生成标注图 |
| `Workbench/LabelInspectionControl.cs:1172` | `new BarcodeComparisonControl(LastRequest.Actual, barcode)` |

### 3.3 对照验收点

| 验收点 | 现状 | 证据 |
|---|---|---|
| `FromVision` 跑完检测且不保存图像时，不访问惰性快照 | **已满足，且已有可执行证据** | 第 1 类 11 处全是 `??` 兜底；元数据成员（`ImageWidth/Height`、`ReferenceWidth/Height`、`HasReference`）走 Vision 分支。**新增用例** `RealBackendReadsThroughLeaseWithoutMaterializingSnapshot`：真实 `OpenCvInspectionBackend` + 真实 Vision 租约跑完一个 `Blank` 区质检，断言逐行读租约次数 > 0（证明真读了像素、用例不空转）而**整帧复制次数 = 0** |
| 请求释放后，不存在后台任务继续使用其租约 | **已验证** | `Dispose()` 释放两个 Vision 租约（`:218-231`）；**新增用例** `RequestReleasesLeaseExactlyOnceAndRefusesUseAfterDispose` 用真实租约计数证明：宿主丢弃自己的句柄后请求租约仍有效 → `Dispose()` 后未归还租约数归零 → 重复 `Dispose()` 不会多归还 → 释放后 `VisionSource`/`VisionReference`/`Actual`/`Reference` 四个入口全部抛 `ObjectDisposedException` |

#### 3.3.1 新增用例与变异验证（`tests/DP.LabelInspection.Tests/Configuration/VisionLeaseLifecycleTests.cs`）

两条用例都用**真实后台与真实租约**，不用替身对象（避免"测试测的是假对象"）。为证明用例不是"碰巧绿"，对生产代码做了两次变异并确认各自只让对应用例变红：

| 变异 | 位置 | 观察到的红灯 |
|---|---|---|
| A：提前物化惰性快照（构造后立刻 `_ = _convertedActual.Value`） | `InspectionRequest.cs:85` | `WholeFrameCopies` 实测 **1**，期望 0 → 验收点 1 用例失败 |
| B：`Dispose()` 漏掉 `_visionActual?.Dispose()` | `InspectionRequest.cs:225` | `LiveLeases` 实测 **2**，期望 1 → 验收点 2 用例失败 |

两次变异均已**逐字还原**，`git diff -- src/…/InspectionRequest.cs` 为空。
全量测试（含新用例）：`net8.0-windows` **280/280** 通过、`net48` **280/280** 通过（原基线 278 例）。


---

## 4. 第 1 步的候选清单（供审查，本轮不动代码）

### 4.1 收敛 `Actual/Reference`

1. **`SaveReport` 的读取**：**本文件 3.2 原先写的"改成 `AlgorithmContractAdapter.ToLabel(...)`"在现有分层下做不了**（见 4.4）。
   正确形态已由仓库文档规定：**调用方传入自己准备的快照**（`docs/node-packaging/02-node-contract-and-host.md:52`）。
   归入**第 2 步**，不在第 1 步做——`SaveReport` 目前**没有产品调用者**（见 4.4），产品运行期不触发 `Lazy`。
2. **给旧构造器加 `[Obsolete]` 或收窄可见性**，只留给 2 个工具与测试，逐步清零。
3. 删第 1 类的 11 处 `??` 兜底（顺序：`Location` → `Quality` → `Anomaly` → `RoiSession`）。
   **不要直接删成 `!`**：先在 `OpenCvInspectionBackend.OpenCvRoiSession.cs:15` 的 `OpenSession` 加一条
   前置断言（`VisionSource == null` 时抛出指向明确的错误），再删——否则漏掉的旧调用者会变成 N 处空引用。
4. **前置依赖**：删兜底前要先定两个工具的去向（见 4.4 第 4 条）——它们从标签快照建旧请求，
   而旧请求每经过一个调用点就要整帧复制一次。

### 4.2 `InspectionSourceExtensions.cs:24-78`：**是公开契约，不删**

它不是待清理的遗留物，而是**文档承诺的宿主入口**：

- `README.md:99` ——「客户图像入口统一为 `DP.Vision.IImageSource`……**转换会复制**」；
- `README.md:118` —— 宿主示例就是 `engine.InspectAsync(source, recipe, token)`；
- `docs/node-packaging/01-current-capabilities.md:38` —— 列为「更适合节点的统一图像源入口」；
- `docs/node-packaging/README.md:29`、`SOURCE_INDEX.md:14` 均登记在册。

所以：

1. **不删**（删了违约）；
2. **不算"隐性"复制源**——注释、README、capabilities 三处都明示了复制，是本清单原先分类有误；
3. 若要改成走 `FromVision` 消掉这次复制，**必须先做一个判定实验**：旧构造器 + 标签快照时报告天然独立于输入源；
   换成 `FromVision` 后请求持租约，报告是否仍独立（源释放后证据仍可读）**必须用测试证明**。
   实验通过才改，不通过就保留复制并把成本写进文档。


### 4.3 本轮明确不做

- 资产类型改名（第 2 步）、`png_base64` 等格式调整；
- 仿射定位与仿射 UI（第 4 步）；
- 训练/预览路径的类型收敛（第 3 步）——`CharacterAnomalyDetector.VisionLines` 的样板可以先照抄，但不动调用方。

### 4.4 事实更正记录（2026-09-29 复核，推翻了本文件早先的两条建议）

第 1 项与第 2 项的原建议是在没查依赖方向与调用面之前写的。复核结果如下：

1. **分层阻断**：`src/DP.LabelInspection.Storage/DP.LabelInspection.Storage.csproj` 只引用
   `Contracts` + `Core`，**不引用 `Adapter.Vision`**。`AlgorithmContractAdapter` 在 `Adapter.Vision`，
   **Storage 看不见它**，所以「`SaveReport` 改成显式 `ToLabel`」**不可实现**。
   另外 `IImageCodec.EncodePng` 只有 `EncodePng(PixelSnapshot)` 一个重载（`IImageCodec.cs:18`）。
   > 补：加 `EncodePng(IImageSource)` 重载技术上可行（Contracts 能看见 `DP.Vision.IImageSource`），
   > 但 PNG 编码终究需要一块连续缓冲，复制只是**从请求里搬到编解码器里**，并没有消失。
   > 且 `CvImages.Mat` 目前只有 `Mat(PixelSnapshot)` 一个入口（`CvImages.cs:10`）。

2. **`SaveReport` 没有产品调用者**：全仓调用点 = `tools/…/BarcodeRegression/Runner/Program.cs:50`、
   `samples/…/Demo.WinForms/Runner/Program.cs:194`、测试 7 处。
   两个 UI 工程（`src/DP.LabelInspection`、`src/DP.LabelInspection.Wpf`）**都不保存报告**。
   → **产品运行期不会触发 `Lazy`**；第 1 项不构成第 1 步的阻塞。

3. **产品里唯一触发 `Lazy` 的地方是 UI 显示**：
   `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:1172`
   `new BarcodeComparisonControl(LastRequest.Actual, barcode)`——打开码对比卡片时读一次整帧。
   属「UI/预览」（分类 E），是**第 3 步**范围。两个 UI 的请求都来自 `FromVision`
   （WinForms `:825`、WPF `:443`）。

4. **两个工具的建请求方式**：`BarcodeRegression`（`new InspectionRequest(frame, recipe)`）与
   `CompatibilityProbe`（`new InspectionRequest(actual, recipe)`）都从**标签快照**建旧请求。
   若运行时不再接受快照请求，它们要么改成 `FromVision(Bridge.ToVision(snapshot), …)`，
   要么保留快照入口。**注意收益方向**：快照请求每经过一个调用点就整帧复制一次
   （`Bridge.ToVision(_request.Actual)` 本身是复制），多 ROI 会重复；改成租约请求后这些复制全部归零。
   → 这两个工具的迁移应排在「删 11 处兜底」**之前**。

5. **`InspectionSourceExtensions` 是对外公布的契约**，不是遗留物（证据见 4.2）。
   原先"若确认无外部宿主则删除"的选项**作废**。

---

### 4.5 第 1 批执行结果（2026-09-29，已动手改代码）

已按「打通唯一运行时输入」完成，**生产代码 5 个文件**：

| 文件 | 变更 |
|---|---|
| `src/DP.LabelInspection.Adapter.Vision/Algorithms/InspectionSourceExtensions.cs` | 内部改用 `InspectionRequest.FromVision`；删除 `ToLabel` 整帧复制；帧与请求在任务作用域内 `using`，成功/取消/异常均归还租约 |
| `tools/…/BarcodeRegression/Runner/Program.cs` | 旧构造器 → `ToVision` + `V.ImageFrame` + `FromVision` |
| `tools/…/CompatibilityProbe/Runner/Program.cs` | 同上 |
| `README.md:99` | 「转换会复制」改为「**检测输入不再复制为标签快照**；证据/持久化/训练资产仍为标签快照」 |
| `docs/node-packaging/01-current-capabilities.md:36,38` | 同步更正 |

**测试**：`SourceInspectionTests.cs` 重写（像素改经 `VisionSource` 读；新增多 ROI 与报告证据独立性用例）；
探针抽为共享替身 `tests/…/Integration/TestDoubles/LeaseProbe.cs`；
`VisionLeaseLifecycleTests.cs` 改用共享探针。

**验证**：`net8.0-windows` **282/282**、`net48` **282/282**；`CompatibilityProbe` 两个 TFM 冒烟
均输出 `OpenCV native OK; x64=True; verdict=Ng; area=900`；
Vision 算法测试 `net8.0` **109/109**（`net48` 65 例失败为**既有原生库放置问题**，
`DP.Vision` 工作区干净且其测试工程缺 `FlattenNet48NativeDependencies` 目标，与 `fe5d237` 之前同型）。

**变异验证**：把 `InspectionSourceExtensions.cs` 回退到旧版后，
`RealEngineAcceptsUnifiedSource` / `MultipleRoisDoNotMaterializeLabelWholeFrame`
均报 `WholeFrameCopies` 实测 **2**（正是旧版对 actual + reference 各调一次 `ToLabel`），
`BothInputsRemainOwnedUntilEngineCompletes` 报 `request.VisionSource` 为 null。
（首次断言顺序让红灯落在 `RowCopies` 上，归因不准，已调整顺序让红灯命名缺陷本身。）

**第 2 批精确删除清单**见仓库根 `MIGRATION_BATCH2_TARGETS.md`。

---

### 4.6 第 2 批执行结果（2026-09-29，已完成）

**目标**：删除 `InspectionRequest` 里的双轨图像，让运行时像素只有 Vision 租约一条来源。

| 提交 | 内容 |
|---|---|
| `d7505d9` | 2a：39 处旧构造器调用点 → `TestRequests.FromSnapshot`；`OpenSession` 加租约前置断言 |
| `bc1ea2f` | 2c：删 11 处 `?? Bridge.ToVision(_request.Actual/Reference)` 兜底 |
| `c124561` | anomaly 路径下线旧 `PixelSnapshot` 入口（原先只是躺在工作区未提交，非他人改动） |
| `70929f6` | 2b：删 `_actualSnapshot`/`_referenceSnapshot`/两个 `Lazy`/旧公开构造器/`Actual`/`Reference`；`VisionSource` 改非空 |

**做法要点（与 4.1 早先设想不同，且更省）**：没有改 `SaveReport` 签名，而是在 `InspectionRequest` 上新增两个
**显式命名**的复制方法 `CreateActualSnapshot()` / `CreateReferenceSnapshot()`。复制从"读属性时悄悄发生"
变成"调用方点名要求、每次调用都复制"，而 8 处 `SaveReport` 调用点**一处都不用改**。
`SaveReport` 顺带修掉了参考图被复制两次。

**结果**：全量构建 0 错误（2 个既有警告）；net8.0-windows 与 net48 均 **283/283**。
（283 = 284 − 1：`OpenSessionInputTests` 的"旧请求被拒"用例随旧构造器删除而消失——编译期已不可能构造出那种请求。）

**对 3.2 的收束**：本节早先列的"请求内惰性快照"读取方已全部迁移完；
`grep -rn "Bridge.ToVision(_request" src` = **0**，`grep -rn "new InspectionRequest(" src tools samples` = **0**。

**尚存（属第 3~5 批，不要误以为已清零）**：`Contracts.PixelSnapshot` 类型本身、
`InspectionStore` 的 PNG 编码路径、报告证据里的标签快照、
`Adapter.Vision` 的 `ToLabel`/`ToVision` 转换原语、UI 预览的 `ToLabel` 调用、训练/字库图像字段。

### 4.7 第 3 批第 1 组：类型改名与定位（2026-09-29，已完成）

**目标**：把"标签侧通用图像"这个身份从类型上剥掉——**只改名和重新定位，不改生命周期**
（保持"继续复印"），不把租约 / `Dispose` 引进报告、字库、训练这些数据模型。

**做法**：`DP.LabelInspection.Contracts.ImageFrame` → `PixelSnapshot`
（文件 `Imaging/ImageFrame.cs` → `Imaging/PixelSnapshot.cs`）。选择**一次性全局改名**而不是逐组改名，
是因为逐组改名必然要求两个类型名并存，而"又多一层适配器、旧类型仍是运行时入口"正是本迁移明令禁止的状态。

**为什么"很大"仍然安全——可被证明的纯机械替换**：

- 76 个 `.cs` 文件、216 处引用；Vision 侧 `DP.Vision.ImageFrame` / `V.ImageFrame` **40 处一处未动**
  （负向断言 `(?<![\w.])` 天然跳过带 `.` 前缀的写法）。
- **纯度证明**：把 HEAD 版本按**字节**套用同一套变换后与工作区逐字节比较，**75/76 完全相同**；
  唯一例外是 `PixelSnapshot.cs`（刻意重写的角色文档）。
- 附带收益：源码里裸写 `ImageFrame` 的 CS0104 歧义消失——`ImageFrame` 现在只指 Vision 侧类型；
  两个为消歧而写的 `using ImageFrame = DP.LabelInspection.Contracts.ImageFrame;` 别名随之删除。

**"定位"是可执行的，不只是注释**：新增 `tests/.../Architecture/PixelSnapshotRoleTests.cs`（3 例）

1. `PixelSnapshot` 不得实现 `IDisposable`（自持像素、无释放语义；一旦可释放，所有持有它的报告/字库/UI 都成泄漏点）；
2. `InspectionRequest` 的构造器与公开方法**不得接受** `PixelSnapshot` 参数（只允许"产出"：`CreateActualSnapshot()`）；
3. `FromVision` 必须接受 `DP.Vision.ImageFrame` 租约。

**变异验证**：给 `InspectionRequest` 临时加一个 `MutationProbe(PixelSnapshot)` 方法 →
**恰好 1 例变红**（`DetectionRequestNeverAcceptsASnapshot`），消息点名 `MutationProbe`，另 2 例保持绿；已逐字还原并 `grep` 确认无残留。

**结果**：全量构建 0 错误（2 个既有警告）；net8.0-windows 与 net48 均 **286/286**（283 基线 + 3 个新守卫）。

> ⚠️ **踩到的坑**：用 Python 默认方式读、再以 `newline=""` 写，会给**原本没有 BOM** 的文件加上 BOM——
> 76 个文件每个都多出一行 `-using … / +﻿using …`，diff 立刻被污染。更麻烦的是：
> "HEAD 套变换后比对"这种纯度检查若用 `utf-8-sig` 解码，会把 BOM 差异一并抹掉，**检查本身失效**。
> 正确做法是全程按**字节**处理与比对。换行符实测未变（`git ls-files --eol` 为 `i/lf w/lf`）。

**尚存（第 3 批后续组，不要误以为已清零）**：训练输入仍以快照为参数
（`IAnomalyModelTrainer.Train`、`RegionAnomalyDetector.Train` / `TrainEntry`）；
报告证据（`RegionAnomalyEvidence.HeatMap`、`CharacterPatch.Patch`、`GlyphComparison.*`）与
字库字段（`GlyphReference.Image`、`GlyphImportItem.Image`、`AnomalyTrainingImage.Image` 等）也仍是快照。
`PixelSnapshot.Crop` 以及 `IImageCodec.EncodePng(PixelSnapshot)`、`CvImages.Mat(PixelSnapshot)`
这些"图像操作"要等第 4 批随编解码 / 绘制一起搬走。

> 其中**"字库参考图每次检测整帧复制"已在 4.8 处理**：参考图现在按固定版本只转换一次，
> 借出方各自持独立租约。字库字段本身仍是快照（缓存从它转换），这一层没有变。

### 4.8 第 2 步样板：文字质检参考图按固定版本只转换一次（2026-09-29，已完成）

**动机（实测）**：`RoiSession.Quality.TextQuality` 原本**每个 ROI、每次检测**把字库里**每一个字**
从 `PixelSnapshot` 经 `Bridge.ToVision` 整帧复制成 Vision 租约，本 ROI 结束时全部释放。
浪费量 = `ROI 数 × 字库字数` 份整帧。`ToLabel` 也确认是**双重复制**
（`AlgorithmContractAdapter.cs:44-51` 先 `CopyTo`，`PixelSnapshot` 构造器再 `Clone`）。

**改动（4 个文件）**

| 文件 | 作用 |
|---|---|
| `Adapter.Vision/Algorithms/IGlyphReferenceImageConverter.cs` | 新增转换缝：`PixelSnapshot → IImageSource`，**可观测**（探针可计数） |
| `Adapter.Vision/Algorithms/VisionGlyphReferenceImageConverter.cs` | 默认实现，委托 `AlgorithmContractAdapter.ToVision` |
| `Runtime/Libraries/GlyphReferenceImageCache.cs` | 版本级缓存：容量 8，缓存自己持一份租约，取用返回 `Retain` 出的独立租约 |
| `Runtime/Libraries/GlyphReferenceLease.cs` | 借出的租约；`GlyphTemplate` 只借用图像，释放责任在租约 |
| `Runtime/.../RoiSession.Quality.cs` | 热路径改为 `_owner._referenceImages.Acquire(library)`，`finally` 只归还本 ROI 那份 |

**为什么没有复用 `PinnedRevisionCache<T>`**：它没有逐出回调（放租约进去会在逐出时泄漏），
且 `Get` 在锁外 `load()`，并发未命中会载入两次并留下被丢弃的重复值——对快照无害，对图像租约就是泄漏。
新缓存把转换放在锁内，**同一版本并发取用只转换一次**。

**为什么缓存是 public 而 `PinnedRevisionCache` 是 internal**：测试程序集没有 `InternalsVisibleTo`，
只有 public 才能直接驱动"容量 1 + 逐出"这种确定性场景。`IGlyphReferenceImageConverter` 则沿用
本仓库"用公开接口装饰来观测"的既有手法（同 `CountingGlyphLibraries`）。

**验收与变异验证**（`tests/.../Text/GlyphReferenceImageCacheTests.cs`、
`tests/.../Integration/FullInspectionTests.ReferenceImages.cs`，共 5 例）

| 用例 | 断言 | 变异 | 红灯数值 |
|---|---|---|---|
| 同版本反复取用 | 转换次数 == 参考图个数 | 缓存不复用 | 2 → **10**（2 字 × 5 次） |
| 容量内多版本 | 8 个版本各一次 | 同上 | 8 → **9** |
| 并发取用同版本 | 3 个参考图只转一次 | 同上 | 3 → **192**（3 × 64） |
| 逐出不影响借出 | 逐出后仍能读到自己的像素 | 借出时不 `Retain` | `ObjectDisposedException` |
| 两次检测两 ROI | 转换次数 == 1 | 绕过缓存 | 比较数 3 → **0** |
| 同上 | 同上 | 缓存不复用 | 1 → **4**（1 字 × 2 ROI × 2 检测） |

**写用例时踩到的坑**：两个 ROI 用**完全相同的 bounds** 会被工作流判 `roi_overlap` 并整体跳过质量阶段，
此时 `libraries.Loads` 仍是 1、`conversions` 却是 0 —— 看起来像"缓存没生效"，实际是 ROI 根本没跑。
多 ROI 用例必须用**互不重叠**的 bounds。

**本轮明确不做（不要误读为已清零）**

- **输出侧 `ToLabel` 不为零**：报告证据仍消费 `PixelSnapshot`，两个界面
  （WinForms `Workbench/LabelInspectionControl.cs:1057-1062`、WPF `:523-528`）直接读它来画。
  强行断言整个 `TextQuality` 零 `ToLabel` 会迫使一次性重写报告。
- **报告"热态租约 + 归档字节"未设计**：`report.json` 走 `FrameConverter` 的 `png_base64`，
  本来就是"序列化时才编码"，且 `SaveReport` 无产品调用者 → 这条路径现在改它没有产品收益。
- **`PixelSnapshot` 未删除**，字库字段 `GlyphReference.Image` 仍是快照（缓存**从它**转换）。
- **Vision 编码器未新增**；`OpenCvImageFileReader` 仍是**路径入口**、保留 BGRA/Gray16、
  **不做透明图合成**，而标签侧 `OpenCvImageCodec.Decode` 是**字节入口**、把 4 通道合成到白底、
  且有 12000×12000 / 16M 像素上限。**两者不等价**，加字节入口必须逐项核对，不能只看都用 `ImDecode`。

## 5. 复核方式



```bash
export APPDATA="C:\\Users\\25845\\AppData\\Roaming"
export ProgramFiles="C:\\Program Files"
# 关掉正在运行的示例程序与 Visual Studio 后再跑全量
dotnet build DP.LabelInspection.sln -c Debug
dotnet test tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj -c Release -f net48 --nologo
dotnet test tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj -c Release -f net8.0-windows --nologo
# 只跑第1步的两条验收用例
dotnet test tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj -c Release -f net8.0-windows --nologo --filter "FullyQualifiedName~VisionLeaseLifecycleTests"
# 只跑 4.8 的参考图缓存验收用例
dotnet test tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj -c Release -f net8.0-windows --nologo --filter "FullyQualifiedName~GlyphReferenceImageCacheTests|FullyQualifiedName~TextQualityReusesConvertedReferenceImages"
```

> ⚠️ 改完生产代码后不要加 `--no-build`：否则跑的是上一次构建的旧二进制（本轮变异验证时就踩到过，误报 3 个失败）。

本轮基线证据留存在 `artifacts/step0-build.log`（44 个锁文件错误）与 `artifacts/step0-sample-out/`（绕锁构建产物）。
