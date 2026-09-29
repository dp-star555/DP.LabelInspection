# 报告证据的生命周期与所有权（设计，待评审）

> 本文件对应迁移计划的**第 3 步**：设计报告的"热态租约 + 归档字节"及 UI 释放责任。
> 它**不改代码**，只把现状测清、把岔路列清、把可验证的验收点定下来。
> 前置：第 2 步（字库参考图按固定版本只转换一次）已完成，见 `IMAGE_OWNERSHIP_INVENTORY.md` 4.8。

## 1. 范围

**要解决**：报告模型里的图像证据现在一律是"独立拥有像素的 `PixelSnapshot`"，于是
①检测热路径必须为每份证据整帧复制，②报告一旦被界面持有，这些像素就一直驻留到下一次检测。

**不在范围内**：字库/训练/候选等**库资产**的图像字段（第 3 批后续组）、`PixelSnapshot.Crop`
与标签侧编解码/绘制代码的搬迁（第 4 批）、`DP.Vision` 编码器（等真实调用方）。

## 2. 现状：报告里到底有哪几张图（实测清单）

`InspectionReport` 本身**不含图像**，`EvidenceGroups`（`InspectionEvidenceGroup`）也**不含图像**——
证据组只是对 `Analysis` / `Findings` 的展示投影。真正的图像只有三处，全部在 `Analysis` 下：

| 证据图 | 声明位置 | 语义 |
|---|---|---|
| `CharacterPatch.Patch` | `Contracts/Text/Segmentation/CharacterPatch.cs:54` | 物理单字图块（移除了已知邻字墨迹） |
| `GlyphComparison.Actual` / `Reference` / `Delta` | `Contracts/Text/Quality/GlyphComparison.cs:52,55,58` | 归一实际 / 参考 / 差异 |
| `RegionAnomalyEvidence.HeatMap` | `Contracts/Inspection/Results/RegionAnomalyEvidence.cs:74` | 与 `Crop` 同尺寸的灰度热力图（可为 null） |

**产出点**（每个 ROI 每次检测都会走）：

- `RoiSession.Quality.cs:229` —— `Bridge.ToLabel(measured.Segmentation)`：**整段分割的全部字符图块**各复制一次。
- `RoiSession.Quality.cs:254` —— 每字符 `Bridge.ToLabel(g.Character.Patch)`。
- `RoiSession.Quality.cs:261-269` —— 每字符最多 3 次 `Bridge.ToLabel(c.Actual / Reference / Delta)`。
- `RoiSession.Anomaly.cs:388, 450` —— `result.HeatMap` 转标签快照。

而 `Bridge.ToLabel`（`AlgorithmContractAdapter.cs:28-52`）**自身是双重复制**：先 `new byte[]` + `CopyTo`，
再被 `PixelSnapshot` 构造器 `Clone` 一次。所以上表每一处"一次转换"实际是 **2 份整块复制**。

## 3. 四条会改变设计的实测结论

### 3.1 显示路径**本来就是同步转成位图**，不长期持有像素

| 界面 | 调用链 | 结果 |
|---|---|---|
| WinForms | `LabelInspectionControl.cs:1057-1062` → `AddPreview`（`:1432-1443`）→ `DrawingImageConverter.ToBitmap` | `PictureBox.Image` 拥有 **Bitmap**，快照当场用完即弃 |
| WinForms（下载单字） | `:1082` → `ToBitmap` | 同上 |
| WPF | `:523-528` → `Preview`（`:770-780`）→ `Source`（`:782-793`） | 转 `BitmapSource` 并 `Freeze()`，快照当场用完即弃 |

**含义**：报告证据的"解码像素"只在**用户真的展开某张卡片的那一瞬间**被需要，
不需要在整个报告生命周期里常驻。这正好支撑"热态拿租约、显示时现取"。

### 3.2 归档态**已经**是编码字节，存储侧不需要改

`InspectionStore._json` 注册了 `FrameConverter : JsonConverter<PixelSnapshot>`
（`InspectionStore.cs:39`），它把每个 `PixelSnapshot` 写成 `{"png_base64": "..."}`（`FrameConverter.cs:33`），
读回时 `codec.Decode`（`:50`）。`SaveReport`（`:427`）就是这样写 `report.json` 的。

**含义**：所谓"归档字节"**不是新增能力，是既有事实**。只要新载体在序列化时仍产出同形状的 `png_base64`，
**历史报告继续可读、新报告应当逐字节不变**——这是一个极强的回归锚点（见 §8）。

### 3.3 算法结果**自己拥有**证据租约，且 Vision 侧已写明"要留下就 Retain"

- `DP.Vision` `TextQualityResult : IDisposable`（"接管分割及单字比较证据，释放结果时一并释放"）。
- `CharacterSegmentation : IDisposable` → 释放其 `CharacterPatch` 集合。
- `CharacterPatch : IDisposable`，其 `Patch` 文档明写：**"若需超出本对象生命周期，应另行Retain"**。
- `GlyphComparisonResult : IDisposable` → 释放 `Actual / Reference / Delta`。

而标签侧现在是 `using var measured = strategy.Inspect(...)`（`RoiSession.Quality.cs:210`），
ROI 一结束就全部释放 —— 所以今天**只能**靠 `ToLabel` 复制才能把证据留下来。

**含义**：热态方案不需要发明新机制，只需在 `measured` 释放**之前**对每份证据 `Retain()` 一次，
把句柄交给一个明确的生命周期对象。**Retain 是引用计数，不是复制。**

### 3.4 唯一真正"跨时间持有像素"的消费者

`BarcodeComparisonControl`（`Codes/BarcodeComparisonControl.cs`）：
构造参数是 `PixelSnapshot actual`，构造里 `actual.Crop(bounds)` **再复制一次**，
并且"展开"按钮的回调（`:95-107`）**在用户点击时才读 `actual`**——这是一次真正的延迟读取。

调用点在 `LabelInspectionControl.cs:1172`：
`new BarcodeComparisonControl(LastRequest.CreateActualSnapshot(), barcode)`。
注意 `CreateActualSnapshot()` 就是**惰性整帧复制**（`InspectionRequest.cs:138`），
即本项目在**产品路径上唯一触发整帧快照**的地方。

**含义**：这个控件是"证据必须活过调用返回"的唯一硬需求，也是新方案必须正面处理的第一个消费者。

## 4. 设计：一个载体，两种状态

### 4.1 载体

把报告证据的字段类型从 `PixelSnapshot` 换成一个**双态**载体：

```csharp
/// <summary>报告证据图：热态持有 Vision 租约，归档态持有编码 PNG 字节；两者恰有一个非空。</summary>
public sealed class EvidenceImage
{
    /// <summary>热态：借用方必须在本对象存活期间使用；归还责任在证据生命周期。</summary>
    public IImageSource? Lease { get; }

    /// <summary>归档态：与历史报告 JSON 中 <c>png_base64</c> 相同的字节。</summary>
    public byte[]? Png { get; }

    public bool IsHot => Lease != null;
}
```

- **不变式**：`(Lease == null) ^ (Png == null)`，构造时校验；这样"既没租约又没字节"不可能出现。
- **JSON 形状不变**：`FrameConverter` 改为 `JsonConverter<EvidenceImage>`，写出仍是 `{"png_base64": "..."}`
  （热态先编码）。**这是兼容性的关键**，也是 §8 的验收锚点。
- **不引入解码依赖**：载体自己**不解码**。归档态的显示由消费层（界面/工具）用既有的
  `IImageCodec` 解码。这样 Contracts 不会反向依赖 Runtime。

### 4.2 所有权：报告不持有，**证据生命周期**持有

不把 `InspectionReport` 改成 `IDisposable`（理由见 §4.5）。改为引擎返回**一对**：

```csharp
/// <summary>一次检测的完整产出：不可变报告 + 报告内热态证据的租约。</summary>
public sealed class InspectionOutcome : IDisposable
{
    public InspectionReport Report { get; }

    /// <summary>归还报告内全部热态证据租约；归还后 Report 只应作为归档数据使用。</summary>
    public void Dispose();
}
```

- 产出侧：`RoiSession` 在 `measured` 释放前 `Retain()` 每份证据，句柄收进一个**证据袋**；
  `RoiWorkflow` 把袋子交给引擎，引擎用它构造 `InspectionOutcome`。
- 消费侧：界面在**已经存在的释放点**归还——WinForms 的 `LastRequest?.Dispose()`
  （`:716, :932, :1469`）旁边加 `LastOutcome?.Dispose()`；WPF 的 `_report = null`
  （`:142, 295, 383, 396, 414, 426, 469, 766`）旁边同样处理。
- **顺序纪律**：先转位图、后释放；释放之后 `Report` 只当归档数据用（读 `Png`，或干脆不再读图）。

### 4.3 四个时刻

| 时刻 | 做什么 | 复制量 |
|---|---|---|
| **产出**（ROI 质检内） | 对 `measured` 的每份证据 `Retain()` | **零复制**（引用计数 +1） |
| **显示**（用户展开卡片） | 从 `EvidenceImage` 取租约 → 直接转 Bitmap / BitmapSource | **零复制**（原来的 `ToLabel` 双份复制消失） |
| **归档**（`SaveReport`） | 热态→`EncodePng` 一次；归档态直接复用字节 | 编码一次（本来就要做） |
| **释放**（下一次检测 / 清空 / 关闭） | 归还证据袋 | 零 |

### 4.4 归档态显示：有界缓存

重新打开历史报告时，证据只有 `Png`。设计为**显示时解码 + 有界缓存**：

- 缓存键：`EvidenceImage` 实例（引用相等）或报告 + 证据路径；容量按"最近显示的 N 张"。
- 缓存值是 `IImageSource` 租约，逐出时归还；显示方 Retain。
- **不要**在打开报告时把所有证据一次性解码——历史报告可能有几十上百张证据图。
- 缓存放在**界面层**（WinForms/WPF 各自），不放进 Contracts。

### 4.5 为什么不直接把 `InspectionReport` 改成 `IDisposable`

三条实测理由：

1. 报告被**广泛传递**：`IInspectionEngine`、`InspectionSourceExtensions`（对外公布的宿主契约）、
   两个工作台、`WpfInspectionCompletedEventArgs`、`Storage`、工具。任一处忘记释放就是泄漏，
   任一处提前释放就是显示期崩溃——而且都在**用户交互时机**上，测试很难全覆盖。
2. 报告**同时是归档输入**（`SaveReport(request, report)`）。归档态不需要生命周期，
   把"可长期保存的数据"和"必须按时归还的句柄"塞进同一个对象，语义会打架。
3. 把句柄单独放在 `InspectionOutcome` 里，"谁负责归还"变成一个**签名上的事实**，
   而不是一句注释里的约定。

## 5. 所有权表（新方案下的完整责任划分）

| 对象 | 持有者 | 归还者 | 归还时机 |
|---|---|---|---|
| 算法结果租约（`measured` 内部） | `TextQualityResult` / `GlyphComparisonResult` / `CharacterSegmentation` | 标签侧 `using`（现状不变） | ROI 质检结束 |
| **报告热态证据租约** | **`InspectionOutcome`** | **宿主**（界面 / 工具 / 测试） | 下一次检测、清空、关闭 |
| 归档 PNG 字节 | `EvidenceImage`（纯托管数据） | 无需归还（GC） | — |
| 显示用 Bitmap / BitmapSource | `PictureBox.Image` / WPF `Image.Source` | 现有 `ClearGallery`（`:1449-1452`）/ WPF 视觉树 | 换卡片、清空 |
| 归档态解码缓存租约 | 界面层缓存 | 缓存自身（容量逐出） | 逐出、关闭 |
| `BarcodeComparisonControl` 的 `actual` | 控件 | 控件 `Dispose` | 控件被移除时 |
| 字库参考图租约 | `GlyphReferenceImageCache` | 缓存（容量 8 逐出 / 后台 Dispose） | 已实现（4.8） |

## 6. 需要签字的决策点

| # | 决策 | 选项 | 建议 | 后果 |
|---|---|---|---|---|
| **D1** | 证据载体 | (a) 双态 `EvidenceImage` (b) 直接 `IImageSource` + 报告 `IDisposable` (c) 全用字节 | **(a)** | (b) 见 §4.5；(c) 显示每次都要解码，把热路径复制换成显示解码 |
| **D2** | 归还责任 | (a) 引擎返回 `InspectionOutcome` (b) 宿主提供 `EvidenceLifetime` 传入 (c) 报告可释放 | **(a)** | (b) 需要改所有宿主入口签名；(c) 见 §4.5 |
| **D3** | 归档格式 | (a) 沿用 `png_base64` (b) 改外部文件 + 引用 | **(a)** | (b) 需要历史报告迁移，收益不明确 |
| **D4** | 归档态显示 | (a) 每次解码 (b) 有界缓存 | **(b)** | (a) 展开卡片会有可感知延迟 |
| **D5** | `BarcodeComparisonControl` | (a) 改持租约、控件 `Dispose` 归还 (b) 构造时立即转 Bitmap，展开窗口再转一次 | **(a)** | (b) 展开窗口需要重新取图，且丢掉了"原图坐标"的语义 |
| **D6** | 契约破坏性 | `CharacterPatch` / `GlyphComparison` / `RegionAnomalyEvidence` 的构造函数签名会变 | 需确认是否有**外部宿主**（非本仓）依赖 | 若"文档即契约"要求保持，需要先加只读兼容属性 |

## 7. 分步实施（每步独立可验收）

1. **先加载体，不动调用点**：新增 `EvidenceImage` + `FrameConverter` 支持它（旧 `PixelSnapshot` 分支保留），
   单测锁住"写出仍是 `png_base64`、读回等价"。此步**行为零变化**。
2. **热态接入**：`InspectionOutcome` + 证据袋；`RoiSession.Quality` / `RoiSession.Anomaly` 改为
   `Retain` + 构造 `EvidenceImage`。**此步开始才有收益**。
3. **消费者迁移**：两个工作台的显示转换加 `IImageSource` 重载（`DrawingImageConverter.ToBitmap`、
   WPF `Source`）；`BarcodeComparisonControl` 改持租约；工具/样例改读 `EvidenceImage`。
4. **归档态显示**：界面层有界解码缓存。
5. **收尾**：报告模型里 `PixelSnapshot` 字段清零 → 才能进第 4 批删类型。

## 8. 验收（可执行的锚点）

| 验收点 | 手法 | 期望 |
|---|---|---|
| **归档逐字节不变** | 改动前后对同一请求 `SaveReport`，比对 `report.json` 的字节 | **完全一致**（因为归档态本来就是 `png_base64`） |
| **历史报告仍可读** | 用改动前生成的 `report.json` 走读取 + 显示 | 不抛异常，证据可显示 |
| 证据租约归零 | 租约探针（`LeaseProbe` 手法）统计证据净租约 | 检测后 = 证据图数量；`Outcome.Dispose()` 后 = **0** |
| 显示无回归 | `verify.ps1` 的 WinForms/WPF `--smoke` 截图 | 与基线一致，`roi_mouse_mapping=True` / `native_wpf=True` |
| 热路径复制下降 | 在第 2 步已有的转换探针上加"证据转换次数" | 每份证据**至多一次** `Retain`，无 `ToLabel` |
| 变异 | 去掉证据的 `Retain` | 释放后显示抛 `ObjectDisposedException` |
| 变异 | 让 `EvidenceImage` 允许两者皆空 | 构造期断言变红 |

## 9. 风险与明确不做

- **最大风险是"归还时机"**：归还太早 → 用户展开卡片时崩溃；太晚 → 租约长期驻留。
  缓解：把归还点绑在**已经存在的**释放点（`LastRequest?.Dispose()`、`_report = null`），
  不新造时机；并补一条"释放后不得再读租约"的断言。
- **`InspectionSourceExtensions.InspectAsync` 是对外公布的宿主契约**，已登记在
  `README.md:99`（契约说明）与 `:118`（示例 `var report = await engine.InspectAsync(source, …)`）、
  `docs/node-packaging/01-current-capabilities.md:38`、`docs/node-packaging/README.md:29`、
  `SOURCE_INDEX.md:15`。返回类型从 `InspectionReport` 变成 `InspectionOutcome` 是**契约变更**，
  必须同步这 5 处文档与示例。
- **本轮不做**：`PixelSnapshot` 删除、Vision 编码器、`OpenCvImageFileReader` 字节入口、
  字库/训练资产字段迁移、`Annotate` 的绘制路径搬迁。
