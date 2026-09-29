# DP.LabelInspection — 项目长期记忆

## 仓库与远端

- 远端 `origin` = `https://github.com/dp-star555/DP.LabelInspection.git`，主分支 `main`。
- 同级依赖 `C:\Data\PiProgects\WorkFlow\DP.Vision`：**源码级 `ProjectReference`，无版本锁定**
  （既非子模块也非包）；`Contracts` 直接引用 `../../../DP.Vision/src/DP.Vision.Algorithms/...`。
- **推送必须换系统 git**（便携版凭据 helper 为空会挂死）：
  `GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never "C:/Program Files/Git/cmd/git.exe" push origin main`，
  再用 `ls-remote origin refs/heads/main` 从远端侧复核。见技能 `git-push-from-sandboxed-agent`。

## 构建

```bash
export APPDATA="C:\\Users\\25845\\AppData\\Roaming"
export ProgramFiles="C:\\Program Files"
dotnet build DP.LabelInspection.sln -c Debug   # 0 错误；2 个既有 MSTEST0032 警告
```

- 每次都要 export，别假设常驻。`Directory.Build.targets` 强制 `PlatformTarget=x64`，不要覆盖。
- ⚠️ 全量构建报几十条 `MSB3021`/`MSB3027`（"文件被 …Demo.WinForms(PID) / Visual Studio(PID) 锁定"）
  **不是编译错误**：数 `error CS[0-9]+` 条数，为 0 就是纯锁文件——关示例程序/VS，
  或 `-o artifacts/xxx` 另输出目录验证。别去改代码。

## 图像所有权：两套类型的角色

- `DP.LabelInspection.Contracts.PixelSnapshot`（原名 `ImageFrame`）= **自持像素的不可变快照，不是 `IDisposable`**
  （守卫测试锁住）。只存像素数据、**不参与检测**。
- `DP.Vision.ImageFrame` / `IImageSource` = **租约**（`Retain`/`Dispose`）。裸写 `ImageFrame` 现在只指 Vision 租约，
  CS0104 歧义已消失。
- `InspectionRequest` 只剩 Vision 租约一条像素来源：`FromVision` 只 Retain；`Actual`/`Reference` 与旧构造器已删。
  需要像素的调用方**显式**调 `CreateActualSnapshot()` / `CreateReferenceSnapshot()`（每次整帧复制，不缓存）。
- 测试建请求统一走 `tests/DP.LabelInspection.Tests/TestRequests.cs` 的 `FromSnapshot`；
  只有"证明旧请求被拒"的用例才直接用旧构造器。
- 完整清单见仓库根 `IMAGE_OWNERSHIP_INVENTORY.md`（4.6 节=第 2 批结果，4.7 节=第 3 批第 1 组结果与"尚存"清单）
  与 `MIGRATION_BATCH2_TARGETS.md`（第 3 批剩余组与第 4~5 批）。

### 迁移期硬事实（易踩）

- 后台入口是 `src/DP.LabelInspection.Runtime/Inspection/OpenCvInspectionBackend.OpenCvRoiSession.cs` 的 `OpenSession`；
  **不是** `Internal/OpenCvInspectionBackend/`（那里是 `RoiSession*.cs` 分部）。它要求 `request.VisionSource != null`，
  否则抛 `ArgumentException`（信息含 `"Vision image lease"`）。
- 第 2 批**顺序**是"先迁移调用点 → 再加断言 → 最后删兜底"；先加断言会一次打破 28 处旧调用点。
- ⚠️ `Adapter.Vision/Algorithms/InspectionSourceExtensions.cs` 是对外公布的**宿主契约，不要删**——
  `README.md:99,118`、`docs/node-packaging/01-current-capabilities.md:38`、`docs/node-packaging/README.md:29`、
  `SOURCE_INDEX.md:14` 均有登记；其中的整帧复制是三处文档明示的行为。
- `Storage` 只引用 `Contracts` + `Core`，**不引用 `Adapter.Vision`** → Storage 里不能调 `AlgorithmContractAdapter.ToLabel`。
- `SaveReport` **没有产品调用者**（2 工具/样例 + 7 测试）；产品运行期唯一触发惰性快照的地方是 UI 显示
  `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:1172`。
- `docs/node-packaging/02-node-contract-and-host.md:52` 规定：启用保存时宿主自己持有与检测完全相同的请求快照，
  **不能从 `UI.LastRequest`、当前选中图或另一周期重建保存输入**。

## 迁移状态

`VISION_MIGRATION.md`（仓库根）记录"Vision 单图像体系迁移"，**进行中、不可据此宣布发布完成**。
"最终删除标签侧通用图像与重复算法代码"分 5 批，每批都必须有**可删除对象**。

- 已完成：`1523bcf`(1) → `d7505d9`(2a 迁 39 处调用点 + `OpenSession` 租约断言) → `bc1ea2f`(2c 删 11 处兜底)
  → `c124561`(anomaly 路径下线旧快照入口) → `70929f6`(2b 删双轨字段与旧构造器)
  → `28fbb4f`(第 3 批第 1 组：`Contracts.ImageFrame`→`PixelSnapshot` 改名重定位 + 3 条角色守卫测试)。
- **第 3 批裁决（用户 2026-09-29）**：报告/训练/字库的图像字段"**继续复印，只改名和定位**"，
  按"训练输入 → 报告证据 → 字库字段"逐组推进。**不要**把这些数据模型改成 Vision 租约——
  真实调用方本来就持有快照，改租约会**增加**整帧复制（实测否掉）。
  改名用**一次性全局替换**（逐组改名会迫使两个类型名并存）；纯度用**逐字节**证明
  （HEAD blob 套同一变换后与工作区按字节比较）。⚠️ 别用 `utf-8-sig`（会抹掉 BOM 差异），
  写文件也别用 Python 默认读 + `newline=""`（会给无 BOM 文件加 BOM，污染 diff）。

## PP-OCR 任务与业务拆分（2026-09-29，阶段 1–3 已完成）

方案在 `DP.Vision/PPOCR_REFACTOR_PLAN.md`（阶段 4–5 未开始）。
提交：`daa3f4c`(Vision 拆检测) + `7b53403`(业务拆检测) + `7971502`(Vision 收敛工程) + `784a9f4`(业务跟随)。

- Vision 侧：执行端已收敛为**单工程** `DP.Vision.PPOcr.Onnx`（`Recognition/` + `Detection/`，
  net48;net8.0-windows，引 `DP.Vision.Algorithms` + OnnxRuntime，**不含 OpenCV**）。
  `PPOcrDetectionInput/Output/Task`（Task 不含 OpenCV）在此；`OnnxTextRegionDetector` 与旧
  `DP.Vision.Onnx`、`DP.Vision.OnnxDetection` **已在阶段 3 删除**。
- 业务侧：`Runtime/Text/Detection/PPOcrDetectionPreparer`（图→NCHW）与 `DbTextRegionCandidates`
  （概率图→候选）。阈值 `.3`/`.6`/64/1000 轮廓**不得重调**。
- 冻结证据：`tools/DP.LabelInspection.PPOcrBaseline`（`--map` 导概率图、`--verify` 对拍）
  + 其 `Baseline/candidates.json`；离线夹具 `tests/…/Text/Fixtures/PpocrDetection/probability.f32.gz`
  （26.7 KB，测试不启动 ONNX）。
- 模型：`LabelInspection/.venv/…/rapidocr_onnxruntime/models/`（det `d2a7720d…`、rec `48fc40f2…`）；
  样例集 `LabelInspection/samples/production`。**`frames/regular-heldout.png` 不存在**。
- ⚠️ 两个既有阻塞项**未处理，需用户拍板**：OcrRegression 发现步骤引用的帧缺失；
  外观覆盖断言现为 `375/370/5/`**`2`**（唯一差异 `ics-heldout/lot` 的 `exceeds` 0→1，OCR 逐行一致）。

## 测试

- `net8.0-windows`：`dotnet test tests/DP.LabelInspection.Tests/... -c Release -f net8.0-windows` → **296/296 全绿**。
- `net48`：工程内已有 `FlattenNet48NativeDependencies` 目标（`AfterTargets="Build"`，把 `$(OutDir)dll\x64\*.dll`
  平铺到输出根，`fe5d237`）→ `dotnet test -f net48` 直接可用，**296/296 全绿**。
  缺这一步会 100 例 `DllNotFoundException: OpenCvSharpExtern`（.NET Framework 不探 `dll/x64`）。
  **`CopyLocalLockFileAssemblies=true` 实测无效**。这是既有环境问题、不是回归（父提交 `dbcece5` 同样失败）。
  备用：把 `artifacts/project-consolidation-package/net48/WinForms` 加进 PATH。
  判定手法见技能 `net48-native-dll-and-test-attribution`。
- ⚠️ **改过源码就别加 `--no-build`**（会跑旧二进制）。
- ⚠️ **多 ROI 用例必须用互不重叠的 bounds**：相同 bounds 会被 `RoiWorkflow` 判 `roi_overlap` 并整体跳过质量阶段，
  症状误导（`libraries.Loads == 1` 但参考图转换次数 `== 0`）。排查：打印 `findings` 的 Code 列表。
- 质检开关是 `InspectionRegion(...).WithTasks(new RoiInspectionTasks(readData:false, checkQuality:true))`
  （由 `RoiWorkflow.cs:109` 的 `config.Tasks.CheckQuality` 决定，不是阈值）；`ERegionKind.Blank` 可免参考图。

### 写这类测试的约定

- 区分"整帧复制"与"逐行读取"：`ToSnapshot` 是 `CopyTo(0, buf, 0, ByteLength)`（一次整帧）；
  `Crop` → `ImageSourceExtensions.CopyRegion` 是**逐行** `CopyTo(rowOffset, buf, 0, rowBytes)`。
  "源偏移 0 + 目标偏移 0 + 长度=整帧字节数"唯一对应惰性快照 —— 用 `IImageSource` 装饰器探针断言。
- 租约计数探针：`DP.Vision.ImageFrame` 构造即 `image.Retain()`、`Dispose()` 即 `Image.Dispose()`，
  记差额即可对齐租约；探针 `Dispose` 要自己加幂等守卫。
- 探针已抽为共享替身 `tests/…/Integration/TestDoubles/LeaseProbe.cs`（`LiveLeases`/`RowCopies`/`WholeFrameCopies`）。
- 验收手法：**变异验证**（故意改坏生产代码，确认用例变红且红灯数值与缺陷机制对得上，再逐字还原 +
  `git diff -- <file>` 确认干净）。见技能 `test-effectiveness-verification`。

## 验证入口

`verify.ps1` = restore(locked-mode) → Release 构建 → net48 + net8.0-windows 测试 → `CompatibilityProbe`（两 TFM）
→ WinForms/WPF `--smoke` 截图 → 校验 `artifacts/*.png.txt` 的 `roi_mouse_mapping=True` / `native_wpf=True`。
可选 `-RecognitionModel -ProductionAssets -OcrOracle` 开启真实 OCR 回归。
