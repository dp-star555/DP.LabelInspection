# 字段绑定与本次任务数据

## WinForms操作

1. 框出单行文字ROI和条码ROI，给它们明确名称，例如`text-part`、`barcode-part`。文字与条码ROI允许交叠或包含，以支持条码附带的人读文字；两路仍独立识别。固定/空白检查与其他检查、同类型ROI仍不可重叠，冲突会列出名称和坐标。
2. 点“字段绑定”，新增行：
   - 待校验ROI：`text-part`
   - 来源类型：`Region`
   - 来源名称：`barcode-part`
3. 若有任务预期，再给文字及条码分别增加`TaskData`绑定，来源字段名例如`PartNumber`。
4. 单独的固定预期仍在“编辑ROI/规则”的`Expected`输入。多个约束独立执行，不互相覆盖。
5. 无后台时可用“本次任务数据”做人工调试：填检测周期编号、来源、有效分钟数，数据每行形如`PartNumber=WAF100014`。
6. 配方保存绑定关系；实际任务值不保存进配方。下一张图像加载时，控件清空上一张的任务数据，防止串料。

选择`Region`是比较另一ROI的原始读数；选择`TaskData`是比较宿主给出的独立预期。不会使用另一个ROI的Expected来代替其读数，不会把条码内容写入OCR结果。

## SDK调用

```csharp
using DP.LabelInspection.Contracts;

var bindings = new[] {
    new FieldBinding("text-part", EBindingSource.Region, "barcode-part"),
    new FieldBinding("text-part", EBindingSource.TaskData, "PartNumber"),
    new FieldBinding("barcode-part", EBindingSource.TaskData, "PartNumber")
};
var recipe = new InspectionRecipe("part", frame.Width, frame.Height,
    EInspectionMode.Free, EAlignmentMode.AssumeAligned, regions, bindings: bindings);

// 由宿主先获取实际相机周期和MES数据，不能用OCR反推业务预期。
var snapshot = new TaskDataSnapshot(dataCycleId, "MES/work-order",
    acquiredAt, validUntil, values);
var request = new InspectionRequest(frame, recipe,
    cycleId: cameraCycleId, taskData: snapshot);
var report = await engine.InspectAsync(request, cancellationToken);
```

`values`为`IReadOnlyDictionary<string,string>`，构造快照时复制。`cameraCycleId`来自当前采集任务，必须与`dataCycleId`精确一致；宿主负责二者确实对应同一物理工件。仅编号相同不能证明外部系统没有错配。

WinForms/WPF宿主也可按顺序调用：

```csharp
control.SetActualImage(frame, clearRegions: false);
control.SetBindings(bindings);
control.SetTaskData(cameraCycleId, snapshot);
await control.RunInspectionAsync();
```

控件每次`SetActualImage`均清空上次任务数据，即使保留ROI。后台接口调用与界面设置必须遵循控件UI线程规则。WPF有上述宿主接口；可视绑定编辑器本次在WinForms提供。

WinForms环境同时使用`System.Windows.Forms`时，枚举可写完整名称`DP.LabelInspection.Contracts.EBindingSource`，避免与WinForms同名组件冲突。

## 判定

- 可靠读数相同：`binding_match`，内容约束OK。
- 可靠读数不同：`binding_mismatch`，NG。
- OCR置信度不足、质量不足、数据缺失/过期/尚未生效、周期不匹配、来源结果缺失、条码零个或多个：`binding_review`。
- 多个条码即使值相同也不默认选一个，应缩小ROI到唯一符号。
- 默认Ordinal精确比较，区分大小写与空格，不做截取、去前缀或O/0纠正。复合条码字段解析尚未实现。
- 双向绑定不会递归：始终读取原始观测，所以不会无限循环，也不会互相补出不存在的数据。
- ROI互相一致只证明交叉一致性，不能证明两边没有同时打印错。若有可信任务预期，应分别校验两边。
- 内容比较不会证明外观、尺寸或整标签覆盖合格。

新绑定会替代目标ROI无条件的`ocr_identity_review`提示，但不会删除其他内容规则、质量、外观或覆盖问题。

## 后台集成边界

SDK已支持本次请求的动态数据注入，并不内置特定MES、数据库或PLC驱动。宿主负责访问数据源、鉴权、重试、超时以及工件关联，然后传入快照。原生算法执行期间不再读取变化中的后台数据。

界面“本次任务数据”是人工调试入口，不是已连接某个MES。当前逐图批量没有后台取数器；绑定了TaskData却未逐图注入数据的请求明确REVIEW，不复用上一笔。

## 持久化与验证

- 配方JSON新增`bindings`；旧配方缺少该字段时默认无绑定。
- 报告目录新增`task-context.json`，保留周期、来源、采集/失效时间及原始数据快照，纳入哈希清单与ZIP。
- 结果保留原始OCR/条码读数，绑定证据显示目标、来源字段、两边数据及结论。
- 任务上下文和报告含生产标识，按敏感业务数据管理。
- 自动测试覆盖精确比较、O/0、低置信度、低质量、缺失/多码、周期错配、过期/未来时间、复制快照、配方回读、报告ZIP及双向独立约束。
- 实际WinForms smoke用ONNX识别`A1020`、ZXing解码生成QR，并与任务字段交叉核对；随后切换图像，验证旧数据不会复用。
