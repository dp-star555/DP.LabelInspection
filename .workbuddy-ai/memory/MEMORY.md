# DP.LabelInspection — 项目长期记忆

## 仓库与远端

- GitHub 远端 `origin` = `https://github.com/dp-star555/DP.LabelInspection.git`，主分支 `main`。
- 同级依赖：`C:\Data\PiProgects\WorkFlow\DP.Vision`（**源码级 `ProjectReference`，无版本锁定**）；
  `Contracts` 直接引用 `../../../DP.Vision/src/DP.Vision.Algorithms/...`。
- **推送必须换系统 git**（便携版凭据 helper 为空会挂死）：
  `GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never "C:/Program Files/Git/cmd/git.exe" push origin main`，
  再用 `ls-remote origin refs/heads/main` 从远端侧复核。细节见技能 `git-push-from-sandboxed-agent`。

## 构建（每次都要设，别假设常驻）

```bash
export APPDATA="C:\\Users\\25845\\AppData\\Roaming"
export ProgramFiles="C:\\Program Files"
dotnet build DP.LabelInspection.sln -c Debug     # 0 错误；2 个既有 MSTEST0032 警告
```

`Directory.Build.targets` 强制 `PlatformTarget=x64`，不要覆盖成 x86/AnyCPU。

### ⚠️ 全量构建报 `MSB3021`/`MSB3027` 不是编译错误

症状：几十个 `无法将 X 复制到 Y … 文件被"DP.LabelInspection.Demo.WinForms (PID)"或
"Microsoft Visual Studio (PID)"锁定`。**判别**：数一下 `error CS[0-9]+` 的条数，
**为 0 就是纯锁文件**——样例程序在运行 / VS 开着，占用了 `samples/…/bin/` 下的 DLL。
**别去改代码**；先关示例程序和 VS，或把该工程构建到别的输出目录（`-o artifacts/xxx`）验证。

## 图像所有权与迁移边界（2026-09-29 分析）

- `DP.LabelInspection.Contracts.ImageFrame` = **独立拥有像素的不可变快照**（构造即 Clone，无 Dispose）；
  `DP.Vision.ImageFrame`/`IImageSource` = **租约**（`Retain`/`Dispose`）。
- `InspectionRequest` 双入口：旧构造器持快照；`FromVision` 只 Retain，
  `Actual`/`Reference` 首次访问时才整帧复制（`InspectionRequest.cs:85,87,117-128`）。
- 运行时读取快照共 **11 处**，全是 `VisionSource?.Retain() ?? Bridge.ToVision(Actual)` 兜底
  （`RoiSession{,.Location,.Quality,.Anomaly}.cs`）；**Vision 请求不会触发复制**。
  无条件读取只有 `InspectionStore.SaveReport`（`:450-453`）与 UI/样例。
- 最大的隐性整帧复制源：`Adapter.Vision/Algorithms/InspectionSourceExtensions.cs:24-78`
  （宿主向统一图像源入口，先复制成快照再建旧请求；**仓库内只有测试调用**）。
- 完整清单见仓库根 `IMAGE_OWNERSHIP_INVENTORY.md`。

### 依赖方向与"文档即契约"（2026-09-29 复核，推翻了两条早先建议）

- **`Storage` 只引用 `Contracts` + `Core`，不引用 `Adapter.Vision`** →
  Storage 里**不能**调 `AlgorithmContractAdapter.ToLabel`。所以"把 `SaveReport` 改成显式 `ToLabel`"做不了。
  `IImageCodec.EncodePng` 也只有 `EncodePng(ImageFrame)` 一个重载；
  加 `IImageSource` 重载可行（Contracts 能看见 `DP.Vision.IImageSource`），但 PNG 编码终究要连续缓冲，
  复制只是搬家。`CvImages.Mat` 只有 `Mat(ImageFrame)`。
- **`SaveReport` 没有产品调用者**（只有 2 个工具/样例 + 7 个测试）；两个 UI 都不保存报告。
  → 产品运行期不触发 `InspectionRequest` 的惰性快照。
- **产品里唯一触发惰性快照的地方是 UI 显示**：
  `src/DP.LabelInspection/Workbench/LabelInspectionControl.cs:1172`
  `new BarcodeComparisonControl(LastRequest.Actual, barcode)`（第 3 步范围）。
- ⚠️ **`InspectionSourceExtensions.InspectAsync` 是对外公布的宿主契约，不是遗留物**——
  `README.md:99,118`、`docs/node-packaging/01-current-capabilities.md:38`、
  `docs/node-packaging/README.md:29`、`SOURCE_INDEX.md:14` 都有登记。
  **不要删**；里面的整帧复制是三处文档明示的契约行为（"转换会复制"），
  所以它不算"隐性"复制源。要消掉复制必须先做判定实验（证明报告证据不借用输入源）。
- **文档已规定 `SaveReport` 的正确形态**：`docs/node-packaging/02-node-contract-and-host.md:52`
  —— 启用保存时**宿主自己持有与检测完全相同的请求快照**（从保留的源构造一次标签快照 + `InspectionRequest`），
  且明确"**不能从 `UI.LastRequest`、当前选中图或另一周期重建保存输入**"。
- **两个工具从标签快照建旧请求**（`BarcodeRegression`、`CompatibilityProbe`）；
  快照请求每经过一个调用点就整帧复制一次，多 ROI 会重复 → 它们迁移到租约请求是**真收益**，
  且应排在"删 11 处兜底"之前。
- **删 11 处兜底的手法**：不要直接删成 `!`，先在
  `OpenCvInspectionBackend.OpenCvRoiSession.cs:15` 的 `OpenSession` 加前置断言
  （`VisionSource == null` 时抛明确错误 + 测试锁住错误信息），再删。

### 标签侧图像退场：批次进度（2026-09-29）

**第 1 批已完成（「打通唯一运行时输入」）**：

- `Adapter.Vision/Algorithms/InspectionSourceExtensions.cs` 内部改用 `InspectionRequest.FromVision`，
  **公开签名未变**，不再复制标签快照。
- `BarcodeRegression`、`CompatibilityProbe` 已迁到 Vision 输入 →
  **全仓 `new InspectionRequest(` 只剩测试调用**，src/tools/samples 已清零。
- ⚠️ 该文件同时可见 `Contracts.ImageFrame` 与 `DP.Vision.ImageFrame`，必须 `using V = DP.Vision;`。
- 测试探针已抽为共享替身 `tests/…/Integration/TestDoubles/LeaseProbe.cs`
  （`LiveLeases` / `RowCopies` / `WholeFrameCopies`），两个测试类共用。
- 第 2 批精确删除清单见仓库根 `MIGRATION_BATCH2_TARGETS.md`。

**第 2 批已完成**（`70929f6` 收口）：`InspectionRequest` 只剩 Vision 租约一条像素来源，
`Actual`/`Reference` 已删，`VisionSource` 非空。

**第 3 批第 1 组已完成**（`28fbb4f`）：`Contracts.ImageFrame` → `Contracts.PixelSnapshot`
（76 文件 / 216 处，Vision 侧 40 处一处未动），角色定位写进类型文档，新增
`tests/.../Architecture/PixelSnapshotRoleTests.cs`（3 例）把"不是检测输入 / 无释放语义 / 由租约构造"钉成断言。
纯度证明用**逐字节**比对：75/76 纯改名。
⚠️ 批量改名脚本**不要**用 Python 默认读 + `newline=""` 写（会给无 BOM 文件加 BOM，污染 diff）；
纯度检查也必须按字节比对（`utf-8-sig` 会把 BOM 差异抹掉，检查等于失效）。

**Vision 仓库的 net48 测试**：`DP.Vision.Algorithms.Tests` 在 net48 下有 65 例失败，
是**既有原生库放置问题**（该测试工程缺 `FlattenNet48NativeDependencies` 目标），net8 全绿 109/109。

### 写这类测试的约定（2026-09-29 验证）

- **`ImageFrame` 的歧义已在第 3 批消除**（2026-09-29）：标签侧 `Contracts.ImageFrame` 改名为
  `PixelSnapshot`，裸写 `ImageFrame` 现在只指 `DP.Vision.ImageFrame`。写新测试时用 `PixelSnapshot`
  指标签快照、`V.ImageFrame` 或 `DP.Vision.ImageFrame` 指租约；`using V = DP.Vision;` 的别名可留可去。
- **区分"整帧复制"与"逐行读取"**：`InspectionRequest.ToSnapshot` 是
  `CopyTo(0, buf, 0, ByteLength)`（一次整帧）；`Crop` → `ImageSourceExtensions.CopyRegion`
  是**逐行** `CopyTo(rowOffset, buf, 0, rowBytes)`。所以"源偏移 0 + 目标偏移 0 + 长度=整帧字节数"
  这个签名唯一对应惰性快照 —— 用 `IImageSource` 装饰器探针（`Retain` 返回子装饰器、共享计数器）即可断言。
- **租约计数探针**：`DP.Vision.ImageFrame` 构造即 `image.Retain()`、`Dispose()` 即 `Image.Dispose()`，
  记 `Retain`/`Dispose` 差额即可精确对齐租约；探针的 `Dispose` 要自己加幂等守卫。
- **让质检真的跑起来**：开关是 `InspectionRegion(...).WithTasks(new RoiInspectionTasks(readData:false, checkQuality:true))`
  —— 由 `RoiWorkflow.cs:109` 的 `config.Tasks.CheckQuality` 决定，**不是**由对比度/清晰度阈值决定。
  用 `ERegionKind.Blank` 可免参考图（`Fixed` 质检无参考时会 `Reference!` 空引用）。
- ⚠️ **改过源码就别加 `--no-build`**：会跑上一次构建的旧二进制（本次误报 3 个失败）。
- ⚠️ **多 ROI 用例必须用互不重叠的 bounds**：完全相同的 bounds 会被 `RoiWorkflow` 判 `roi_overlap`
  并**整体跳过质量阶段**。症状极具误导性——`libraries.Loads == 1`（字库确实载入了）但
  参考图转换次数 `== 0`，看起来像"缓存/新代码没生效"，实际是 ROI 根本没跑到质检。
  排查手法：把 `findings` 的 Code 列表打出来看（本次靠 `Assert.AreEqual(-1, ...)` 的失败消息取回现场）。
- 有效用例的验收手法：**变异验证** —— 故意改坏生产代码，确认对应用例变红且红灯数值与缺陷机制对得上，
  再逐字还原并用 `git diff -- <file>` 确认干净。见技能 `test-effectiveness-verification`。

## 测试：net8 正常，net48 需要原生库在 PATH

- `net8.0-windows`：`dotnet test tests/DP.LabelInspection.Tests/... -c Release -f net8.0-windows` → 全绿（当前 286 例）。
- **`net48` 直接跑会大面积失败**（当前 100 例），错误全是
  `DllNotFoundException: 无法加载 DLL“OpenCvSharpExtern”`。
  原因：包只把原生库放在 `dll/x64`、`dll/x86`，而 .NET Framework 的 `DllImport` 不探这两个目录。
- **正确跑法**：`tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj` 里已有
  `FlattenNet48NativeDependencies` 目标（`AfterTargets="Build"`，把 `$(OutDir)dll\x64\*.dll` 平铺到输出根目录，
  `fe5d237` 提交），构建后 `dotnet test -f net48` 直接可用（实测 **286/286 全绿**）。
  为什么必须补这一步：NuGet 只对**可执行工程**平铺原生库，非可执行的测试工程不会；
  **`CopyLocalLockFileAssemblies=true` 实测无效**，别走那条路。
- 备用跑法（不想改工程时）：把已平铺原生库的目录加进 PATH：

```bash
export PATH="/c/Data/PiProgects/WorkFlow/DP.LabelInspection/artifacts/project-consolidation-package/net48/WinForms:$PATH"
```

- **这是既有环境问题，不是回归**：用父提交 `dbcece5` 在旁路同级目录重建后同样失败（94 例，
  同型错误）。判定手法见技能 `net48-native-dll-and-test-attribution`。
- 样例/工具工程的 net48 输出根目录里**恰好有**平铺的 `OpenCvSharpExtern.dll`（打包脚本
  `package.ps1` 甚至会把 `dll/x64` 下的重复副本删掉，说明平铺才是交付约定），
  所以它们的冒烟测试能跑；测试工程没有这一步。

## 验证入口

`verify.ps1` = restore（locked-mode）→ Release 构建 → net48 + net8.0-windows 测试 →
`CompatibilityProbe`（两个 TFM）→ WinForms/WPF `--smoke` 截图 → 校验
`artifacts/*.png.txt` 里的 `roi_mouse_mapping=True` / `native_wpf=True`。
可选参数 `-RecognitionModel -ProductionAssets -OcrOracle` 开启真实 OCR 回归。

## 迁移状态

`VISION_MIGRATION.md`（仓库根）记录"Vision 单图像体系迁移"，明确写着**进行中、不可据此宣布发布完成**。

"最终删除标签侧通用图像与重复算法代码"分 5 批；每批都必须有**可删除对象**，
不能停在"又多一层适配器、旧类型仍是运行时入口"。

**第 1、2 批已完成，第 3 批第 1 组已完成**：`1523bcf`（第 1 批）→ `d7505d9`（2a 迁移 39 处调用点 + `OpenSession` 租约断言）
→ `bc1ea2f`（2c 删 11 处兜底）→ `c124561`（anomaly 路径下线旧快照入口）
→ `70929f6`（2b 删双轨字段与旧构造器）→ `28fbb4f`（第 3 批第 1 组：`Contracts.ImageFrame` 改名
`PixelSnapshot` 并重新定位 + 3 条角色守卫测试）。测试计数 **286/286**（net8.0-windows 与 net48）。

**第 3 批的裁决与做法（用户 2026-09-29 决定）**：报告/训练/字库的图像字段"**继续复印，只改名和定位**"，
按"训练输入 → 报告证据 → 字库字段"逐组推进。**不要**把这些数据模型改成 Vision 租约——
真实调用方本来就持有快照，改成租约会**增加**整帧复制（实测否掉了这条设计）。
改名采用**一次性全局替换**（逐组改名会迫使两个类型名并存，正是迁移禁止的状态）；
纯度用**逐字节**证明：HEAD blob 套同一变换后与工作区逐字节比较（注意别用 `utf-8-sig`，会把 BOM 差异抹掉）。

第 2 批后 `InspectionRequest` 只剩 Vision 租约一条像素来源：`Actual`/`Reference` 已删，
`VisionSource` 非空；`grep -rn "Bridge.ToVision(_request" src` = 0，
`grep -rn "new InspectionRequest(" src tools samples` = 0。
需要像素的调用方改为**显式**调用 `CreateActualSnapshot()` / `CreateReferenceSnapshot()`（每次调用都复制整帧，不缓存）。

第 3 批剩余组与第 4~5 批（报告证据、字库字段、编解码与绘图归位、删旧类型）见仓库根
`MIGRATION_BATCH2_TARGETS.md` 与 `IMAGE_OWNERSHIP_INVENTORY.md`
（后者 4.6 节 = 第 2 批结果，4.7 节 = 第 3 批第 1 组结果与"尚存"清单）。

### 迁移期硬事实（易踩）

- 后台入口是 `src/DP.LabelInspection.Runtime/Inspection/OpenCvInspectionBackend.OpenCvRoiSession.cs`
  的 `OpenSession`；**不是** `Internal/OpenCvInspectionBackend/`（那里是 `RoiSession*.cs` 分部）。
  它现在要求 `request.VisionSource != null`，否则抛 `ArgumentException`（信息含 `"Vision image lease"`）。
- `Contracts.PixelSnapshot` **不是 `IDisposable`**（自持像素，无 Dispose；已有守卫测试锁住这条不变量）；
  `DP.Vision.ImageFrame` 才是租约。该类型原名 `ImageFrame`，第 3 批改名并重新定位为"只存像素数据、不参与检测"。
- 测试建请求统一走 `tests/DP.LabelInspection.Tests/TestRequests.cs` 的 `FromSnapshot`（与旧构造器参数逐一对应）；
  只有"证明旧请求被拒"的用例才直接用旧构造器。
- 裸写 `ImageFrame` 的 CS0104 歧义**已在第 3 批消失**（标签侧改名为 `PixelSnapshot`）；写新代码直接用
  `PixelSnapshot` 指标签快照即可，不必再加消歧别名（现存别名可留可去）。
- 第 2 批**顺序**是"先迁移调用点 → 再加断言 → 最后删兜底"：先加断言会一次打破 28 处用真实后台的旧调用点。
