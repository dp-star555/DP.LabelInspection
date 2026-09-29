# 第 2 批删除清单：删除检测请求中的双轨图像（精确调用点）

> 基线：第 1 批完成后（`InspectionSourceExtensions` 已走 `FromVision`，两个工具已迁到 Vision 输入）。
> 本文件只列**要删除/要改的确切位置**，供审阅与逐项勾销。

---

## 0. 执行状态与顺序调整（2026-09-29）

**顺序已调整为「先迁移调用点，再加断言」。** 依据是实测而非推测：若先加 `OpenSession`
前置断言，会立刻打破 **28 处**仍走旧构造器、且使用真实 `OpenCvInspectionBackend` 的用例
（`FullInspectionTests` 独占 10 处），与"每步都能单独跑绿"直接冲突。

| 子步 | 内容 | 状态 |
|---|---|---|
| 2a | 迁移 39 处旧构造器调用点 → `TestRequests.FromSnapshot`；`OpenSession` 加前置断言并锁错误信息 | **已完成** `d7505d9` |
| 2c | 执行 B 节：删 11 处 `?? Bridge.ToVision(...)` 兜底 | **已完成** `bc1ea2f` |
| — | anomaly 路径的旧 `PixelSnapshot` 入口下线（原先只是躺在工作区，单独立提交） | **已完成** `c124561` |
| 2b | 执行 A 节：删双轨字段、旧构造器、`Actual`/`Reference`，`VisionSource` 改非空 | **已完成** `70929f6` |

**第 2 批已全部完成。** 提交后全量构建 0 错误（2 个既有警告）；net8.0-windows 与 net48 均 **283/283**。

### 2b 实际做法（与本文档早先设想不同，且更好）

早先设想把 `SaveReport` 改成"由宿主传入快照"，会牵动 8 处调用点。实际改为在
`InspectionRequest` 上**显式命名**两个复制方法：

```csharp
public PixelSnapshot CreateActualSnapshot();      // 复制整帧；每次调用都复制，不缓存
public PixelSnapshot? CreateReferenceSnapshot();  // 无参考时为空
```

这样既达成了本批目标（删掉 `_actualSnapshot`/`_referenceSnapshot`/两个 `Lazy`/旧公开构造器，
`VisionSource` 改非空），又让**复制从"读属性时悄悄发生"变成"调用方点名要求"**，
而且 8 处 `SaveReport` 调用点**一处都不用改**。迁移因此退化成纯改名：
`request.Actual` → `request.CreateActualSnapshot()`，`request.Reference` → `request.CreateReferenceSnapshot()`。

`SaveReport` 顺带修掉了参考图复制两次（原来 `if (request.Reference != null)` 判一次、再用一次）。

### 执行 2b 时发现的事实更正

- **不存在"另一位写者"。** 之前把这 7 个 anomaly 文件当成并发写者的在途改动，
  实际全是本项目自己的未提交工作：`CharacterAnomalyDetector` 与 `RegionAnomalyDetector`
  各自删掉了一个接收 `Contracts.PixelSnapshot` 的 `Inspect` 重载（内部 `Bridge.ToVision` 后转调），
  并同步更新了测试、样例与基准工具。已作为独立提交 `c124561` 落地。
  教训：判断"是不是别人改的"要**看 diff 内容**，不能只看 `git status` 里有它。
- **判据陷阱**：`git status` 显示 `M` 但 `git diff` 只显示很少的行，可能是**行尾符（LF/CRLF）**差异；
  而 `git diff` 的输出会被**你在中间做的重建操作**污染——本次就是因为先用脚本把文件重建成
  "HEAD+改名"，再去 `git diff`，于是误判"这个文件只有我这一处改动"。
  **要看真实改动，必须在改动工作区之前看，或先备份再比对。**
- `git diff` 只看工作区相对**索引**的差异；若对方已 `git add`（`M ` 在第一列），
  必须用 `git diff --cached` 才看得到。本次 7 个文件都是第二列，属工作区改动。

---

### ⚠️ 2b 与 2c 是编译耦合的（已按"先清读取点、再删字段"完成）

删掉 `Actual`/`Reference` 会让**所有**读取点编译失败，其中就包括 2c 的 11 处兜底。
所以真实依赖是：**先清掉全部读取点（2c + 2b-0），再删字段（2b）**。

2c 已把 11 处兜底清零（`grep -rn "Bridge.ToVision(_request" src` = 0）。
`Actual`/`Reference` 的**非兜底真实消费者**也已全部迁移完毕（见 `70929f6`）：
下表保留为记录，实际做法是把 `request.Actual` / `request.Reference` 纯改名成
`request.CreateActualSnapshot()` / `request.CreateReferenceSnapshot()`，**没有**改动 `SaveReport` 签名。

| # | 位置 | 现状 | 迁移方向 |
|---|---|---|---|
| 1 | `Storage/Persistence/InspectionStore.cs:450,451,453` | `SaveReport` 内读 `request.Actual` / `request.Reference` | 按 `docs/node-packaging/02-node-contract-and-host.md:52`：**改由宿主传入**与检测同一份快照，`SaveReport(request, actual, report, annotated)`；8 处调用点跟着改 |
| 2 | `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:1172` | `new BarcodeComparisonControl(LastRequest.Actual, barcode)` | 用 `Bridge.ToLabel(LastRequest.VisionSource!)` 或持有快照 |
| 3 | `samples/…/Interaction/UiInteractionProbe.cs:650,1145,1482` | `request.Actual` / `LastRequest!.Actual` | 同上 |
| 4 | `samples/…/Runner/Program.cs:194` | `codec.Annotate(request.Actual, report)` + `SaveReport(request, report, …)` | 随 #1 改签名 |
| 5 | `tests/…/Configuration/VisionLeaseLifecycleTests.cs:76,77` | `ThrowsExactly<ObjectDisposedException>(() => request.Actual)` | 这两条断言的对象消失，随 2b 删除 |
| 6 | `tests/…/Workflow/RoiWorkflowTests.cs:86,89` | `request.Actual.CopyPixels()` 与 dispose 后抛错 | 改用 `request.VisionSource` |
| 7 | `tests/…/Text/TextRecognitionTests.cs:255`、`Workflow/FieldBindingTests.cs:160` | `input.Actual` / `initial.Actual` | 先确认 `Request(...)` 帮助方法返回的是不是 `InspectionRequest` |

**不是** `InspectionRequest` 的读取点（勿误改）：`glyph.Comparison.Reference/Actual`、
`c.Actual/c.Reference`（比较结果）、`algorithm.LastResult!.Actual`、
`result.Actual/Reference/Delta`（算法结果）、`_config.Reference`（路径字符串）、
`models(key)?.Reference`。

2b 收尾时同时要处理：`OpenSessionInputTests.SnapshotRequestIsRefusedWithExplicitMessage`
在旧构造器删除后无法再构造"旧请求"，该用例随 2b 删除（编译期已不可能）。

2c 实测数据：

- 8 处 `Actual` 兜底为同型字符串，脚本替换（断言命中数 8）；3 处为参考图/`Crop` 变体，手工改。
- 两处"确实需要参考图"的站点改为显式抛出，不再静默转换：
  `translation_alignment_requires_reference`（`RoiSession.Location.cs`）、
  `fixed_quality_requires_reference`（`RoiSession.Quality.cs`）。
- `RoiSession.Location.cs` 的 `Bridge` 别名随之不再使用，已删除。
- ⚠️ **同一条消息里对同一个文件发两个 `Edit` 会互相覆盖**：本次 `RoiSession.Quality.cs`
  的两处改动只落地了一处，靠 `grep` 复核才发现。改同一文件必须串行发 Edit，改完必须 grep 复核。

---

2a 实测数据与两个易错点：

- 旧构造器调用点 **39 处 / 18 个测试文件**；`TestRequests.FromSnapshot` 的参数表与旧构造器逐一对应
  （`actual, recipe, reference, cycleId, taskData, frameId`），因此是**纯改名**，无断言改动。
- 断言位置是 `src/DP.LabelInspection.Runtime/Inspection/OpenCvInspectionBackend.OpenCvRoiSession.cs:15`。
  ⚠️ 不是 `Internal/OpenCvInspectionBackend/OpenCvRoiSession.cs`——该目录下只有 `RoiSession.cs` 及三个分部。
- 变异验证：移除断言 → `SnapshotRequestIsRefusedWithExplicitMessage` 变红且信息为
  "预期异常类型 ArgumentException，但未引发异常"，正向对照 `VisionRequestIsAccepted` 保持绿；
  逐字还原后 `git diff` 只剩预期新增、无残留。
- `Contracts.PixelSnapshot` **不是 `IDisposable`**（自持像素），测试里不能写 `using var snapshot = ...`。
- 引用同一性无需担心：已确认全仓没有对 `request.Actual`/`Reference` 做 `AreSame` 的断言，
  迁移后 `Actual` 返回的是转换副本而非原实例，语义不变。

⚠️ `tests/DP.LabelInspection.Tests/Anomaly/CharacterAnomalyTests.cs` 的调用点**已在工作区迁移但未提交**，
因为该文件同时含有另一位写者的在途 anomaly 迁移改动（5 行）。它会随对方的提交一起落地，
所以 2b/2c 之前需要确认该文件已干净。

---

## A. `src/DP.LabelInspection.Contracts/Inspection/Configuration/InspectionRequest.cs`

| 行 | 现状 | 处理 |
|---|---|---|
| `:12-13` | `_actualSnapshot` / `_referenceSnapshot` 字段 | **删** |
| `:16-17` | `_convertedActual` / `_convertedReference`（`Lazy<PixelSnapshot>`） | **删** |
| `:27-52` | 公开旧构造器 `InspectionRequest(PixelSnapshot actual, …)` | **删** |
| `:85-87` | 私有构造器里的两个 `Lazy` 赋值 | **删** |
| `:117-128` | `ToSnapshot(V.IImageSource)` | **删** |
| `:171-179` | `Actual` 属性 | **删** |
| `:185-193` | `Reference` 属性 | **删** |
| `:131-150` | `ImageWidth` / `ImageHeight` 的 `?? Actual.Width` 兜底 | 改为只读 `_visionActual` |
| `:162` | `HasReference` 的 `_referenceSnapshot != null` 分支 | 改为只判 `_visionReference` |
| `:165-168` | `ReferenceWidth` / `ReferenceHeight` 的 `_referenceSnapshot?.Width` 分支 | 同上 |
| `:196-204` | `VisionSource` 返回 `V.IImageSource?` | 改为**非空** `V.IImageSource`（旧构造器已删，构造时必然存在）→ 编译器保证后续不再需要兜底 |
| `:207-215` | `VisionReference` | 保持可空（无参考时确实为空） |

**保留**：`CheckLayout`（Gray8/Bgr24、≤12000 边长、≤1600 万像素）继续在 `FromVision` 入口执行；不得用空值断言替代它。

---

## B. 11 处运行时兜底：删除 `?? Bridge.ToVision(...)`

| # | 位置 | 现状 | 删除后 |
|---|---|---|---|
| 1 | `RoiSession.cs:218` | `_request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)` | `_request.VisionSource.Retain()` |
| 2 | `RoiSession.cs:228` | 同上 | 同上 |
| 3 | `RoiSession.Location.cs:34` | 同上 | 同上 |
| 4 | `RoiSession.Location.cs:36` | `_request.VisionReference?.Retain() ?? Bridge.ToVision(_request.Reference!)` | 显式检查后 `_request.VisionReference.Retain()`（见下注） |
| 5 | `RoiSession.Quality.cs:35` | `_request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)` | `_request.VisionSource.Retain()` |
| 6 | `RoiSession.Quality.cs:54-55` | `_request.VisionSource?.Crop(…) ?? Bridge.ToVision(_request.Actual.Crop(r.Bounds))` | `_request.VisionSource.Crop(…)` |
| 7 | `RoiSession.Quality.cs:91-96` | `_request.VisionReference?.Crop(…) ?? Bridge.ToVision(_request.Reference!.Crop(original.Bounds))` | 显式检查后 `_request.VisionReference.Crop(…)` |
| 8 | `RoiSession.Quality.cs:200` | `_request.VisionSource?.Retain() ?? Bridge.ToVision(_request.Actual)` | `_request.VisionSource.Retain()` |
| 9 | `RoiSession.Anomaly.cs:267` | 同上 | 同上 |
| 10 | `RoiSession.Anomaly.cs:300` | 同上 | 同上 |
| 11 | `RoiSession.Anomaly.cs:418` | 同上 | 同上 |

> **注（参考图两处）**：`Location.cs:36` 只在 `Mode==Template && Alignment==Translation` 时执行，
> `Quality.cs:96` 只在 `Kind != Blank` 时执行；两处都由 `Validate`（`RoiSession.cs:113-116`）
> 保证已提供参考。因此用**显式抛出**（`?? throw new InvalidOperationException("参考图缺失：<阶段>")`）
> 而不是 `!`，让越界表现为可读错误。

**保留不动**：`RoiSession.cs:121,122,136` 是 `ReferenceWidth/ReferenceHeight/ImageWidth/ImageHeight`
的**元数据**读取，不复制像素；改完 A 节后自然只剩 Vision 分支。

---

## C. 同批必须一起改（否则编不过）

| 位置 | 现状 | 处理 |
|---|---|---|
| `src/DP.LabelInspection.Storage/Persistence/InspectionStore.cs:450,451-453` | `SaveReport` 读 `request.Actual` / `request.Reference` | 改为**调用方传入快照**（`docs/node-packaging/02-node-contract-and-host.md:52` 规定的形态）。Storage 不得为此新增 `Adapter.Vision` 引用 |
| `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:1172` | `new BarcodeComparisonControl(LastRequest.Actual, barcode)` | 改为持 Vision 租约或为显示显式制作的快照（预览形态属第 3 批，但本批必须先改掉才能编译） |
| `samples/…/Demo.WinForms/Runner/Program.cs:194` | `codec.Annotate(request.Actual, report)` | 改为用本地标签快照或显式转换 |
| `samples/…/Demo.WinForms/Interaction/UiInteractionProbe.cs:650` | `var originalFrame = originalRequest.Actual;` | 同上 |
| `samples/…/Demo.WinForms/Interaction/UiInteractionProbe.cs:1145` | `DrawingImageConverter.ToBitmap(request.Actual)` | 同上 |
| `tests/…/Workflow/RoiWorkflowTests.cs:86` | `request.Actual.CopyPixels().Length` | 改为经 `VisionSource` 断言 |
| `tests/…/Workflow/RoiWorkflowTests.cs:89` | `ThrowsExactly<ObjectDisposedException>(() => request.Actual)` | 同上 |
| `tests/…/Configuration/VisionLeaseLifecycleTests.cs:76-77` | 断言 `request.Actual` / `request.Reference` 释放后抛异常 | 删这两条，保留 `VisionSource` / `VisionReference` 两条 |

### `SaveReport` 签名变更的受影响调用者（8 处）

`tools/DP.LabelInspection.BarcodeRegression/Runner/Program.cs:50`、
`samples/DP.LabelInspection.Demo.WinForms/Runner/Program.cs:194`、
`tests/…/Codes/BarcodeEvidenceGroupTests.cs:75`、
`tests/…/Integration/FullInspectionTests.cs:425,462,567`、
`tests/…/Integration/LabelInspectionHostTests.cs:51`、
`tests/…/Workflow/FieldBindingTests.cs:89`、
`tests/…/Workflow/RoiWorkflowTests.cs:349`。

### 旧构造器的测试调用点（需迁移，共 39 处）

`Anomaly/AnomalyLibraryTests.cs:93`、`Anomaly/AnomalyTrainingSessionTests.cs:160`、
`Anomaly/CharacterAnomalyTests.cs:134`、`Architecture/AlgorithmIsolationTests.cs:47,74`、
`Codes/BarcodeEvidenceGroupTests.cs:63`、`Codes/BarcodePrintTests.cs:103`、
`Codes/BarcodeReadabilityTests.cs:34,151,188,236`、`Configuration/ReferenceModeTests.cs:32,134,176`、
`Integration/FullInspectionTests.cs:148,176,228,276,342,413,447,545,632,689`、
`Integration/InspectionTests.cs:59`、`Integration/LabelInspectionHostTests.cs:45,51`、
`Libraries/GlyphQuickLibraryTests.cs:186`、`Text/IndependentTextQualityTests.cs:69`、
`Text/TextRecognitionTests.cs:182,255,276`、`Workflow/ContentVerificationTests.cs:86`、
`Workflow/FieldBindingTests.cs:160,198,227`、`Workflow/RequiredAppearanceTests.cs:27`、
`Workflow/RoiWorkflowTests.cs:41,105`。

> 建议加一个测试辅助（如 `TestRequests.FromSnapshot(snapshot, recipe, reference)`，
> 内部 `Bridge.ToVision` + `V.ImageFrame`），把 38 处收敛成一行调用，避免逐处手写租约管理。

---

## D. 本批明确**不删**（留给第 3～5 批）

| 对象 | 剩余使用点 | 归属 |
|---|---|---|
| `AlgorithmContractAdapter.ToLabel(IImageSource)` | `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:621,645`、`src/DP.LabelInspection.Wpf/Workbench/LabelInspectionControl.cs:283,308` | 第 3 批（UI 预览） |
| `AlgorithmContractAdapter.ToVision(L.PixelSnapshot)` | `tools/…/BarcodeRegression/Runner/Program.cs:50`、`tools/…/CompatibilityProbe/Runner/Program.cs:45` | 第 4 批 |
| `IImageCodec` / `OpenCvImageCodec` / `CvImages` | 存储与工具 | 第 4 批 |
| 报告证据的标签 `PixelSnapshot`（`CharacterPatch`、`GlyphComparison`、热力图等） | `Contracts` 多处 | 第 3 批 |

---

## E. 第 2 批验收

- 双目标构建（net48 + net8.0-windows）+ 两目标测试；当前基线 **282 例**。
- 真实后台**单 ROI / 多 ROI** 均不物化标签整帧：
  `VisionLeaseLifecycleTests.RealBackendReadsThroughLeaseWithoutMaterializingSnapshot`、
  `SourceInspectionTests.MultipleRoisDoNotMaterializeLabelWholeFrame`。
- 入口与请求租约：
  `VisionLeaseLifecycleTests.RequestReleasesLeaseExactlyOnceAndRefusesUseAfterDispose`、
  `SourceInspectionTests.BothInputsRemainOwnedUntilEngineCompletes`（含异常与取消两个 DataRow）。
- **并行 ROI 下的租约平衡**：当前**没有**覆盖 `MaximumParallelRois > 1` 的用例，
  建议本批补一条（多个 ROI 并发时每个 `Retain` 都有对应释放）。
- 报告证据独立性：`SourceInspectionTests.ReportEvidenceDoesNotHoldInputLease`。
