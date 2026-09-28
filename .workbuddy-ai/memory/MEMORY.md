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

## 测试：net8 正常，net48 需要原生库在 PATH

- `net8.0-windows`：`dotnet test tests/DP.LabelInspection.Tests/... -c Release -f net8.0-windows --no-build` → 全绿（当前 267 例）。
- **`net48` 直接跑会大面积失败**（当前 100 例），错误全是
  `DllNotFoundException: 无法加载 DLL“OpenCvSharpExtern”`。
  原因：包只把原生库放在 `dll/x64`、`dll/x86`，而 .NET Framework 的 `DllImport` 不探这两个目录。
- **正确跑法**：`tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj` 里已有
  `FlattenNet48NativeDependencies` 目标（`AfterTargets="Build"`，把 `$(OutDir)dll\x64\*.dll` 平铺到输出根目录，
  `fe5d237` 提交），构建后 `dotnet test -f net48` 直接可用（实测 **267/267 全绿**）。
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
