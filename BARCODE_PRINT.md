# 条码：读码＋打印墨迹检查

## 当前实现

条码ROI默认同时执行两项独立任务：

1. ZXing解码、Expected/业务字段/跨ROI绑定校验。
2. OpenCV一维条纹/QR模块打印检查，输出局部缺墨、多墨及原图缺陷位置。QR实现与边界见[QR_PRINT.md](QR_PRINT.md)。

解码成功现在是`barcode_decoded / OK`信息，不再单凭“未做ISO评级”标REVIEW。质量检查不以解码成功代替合格；即使解码失败，也会尝试检查已定位的一维条纹。明确框选码区且解码器实际执行后未读出时，读取结论NG；解码器未加载或整图探索没找到码仍为REVIEW。打印网格/局部定位不确定时可保留子项REVIEW，不覆盖读取失败的NG。

## 检查方法

- 在原始灰度像素上二值化，定位稳定的水平或垂直条纹主体。
- 用当前条码自身的行间支持估计条/空隙分布，不使用上一张条码，也不把任意重编码结果当成当前印刷真值。
- 对每根条内部和每个空隙分别检查墨迹，计算连通域面积。
- 同时达到面积与局部比例阈值时输出NG：
  - `barcode_missing_ink`：条内部缺墨、孔洞或断条候选。
  - `barcode_extra_ink`：空隙多墨或粘连候选。
- 缺陷框、面积均为待检原图坐标/像素，不是归一画布面积。
- `barcode_print_scope`是检查范围说明，不是ISO评级或整个符号认证。
- `barcode_print_review`表示无法可靠定位、多个码、裁切、低对比度、方向不明确或暂不支持的码制。
- 短的人读文字行与条纹主体按行结构分组，避免把正常附带文字当成条内部缺墨；不能可靠分组时不强行判断。

旧`barcode_scan_review`的整块最大扫描差异已由以上局部检查替代。一维条内另有`DetectInkLoss=True`灰度损失检查，`MinimumInkLoss=0.25`可校准，输出`barcode_ink_loss`，用于发现未越过黑白阈值的墨色变浅。实际漏检诊断及局限见[BARCODE_INK_LOSS.md](BARCODE_INK_LOSS.md)。

## 界面配置

在“编辑ROI/规则”选择条码ROI，展开“条码打印质量”：

| 参数 | 默认 | 含义 |
|---|---:|---|
| BarcodePrintEnabled | True | 是否检查墨迹；False仅读码/内容规则，不产生未启用打印检查的错误 |
| BarcodeMinimumArea | 4 | 单个缺陷的最小原始像素面积 |
| BarcodeMinimumFraction | 0.01 | 缺陷面积÷其所在条/空隙的检查面积，而非整块ROI面积 |
| BarcodeEdgeTolerance | 1 | 条边/空隙边排除像素；按宽度限制，始终保留至少1像素检查宽度 |

阈值是工程初值，需根据相机分辨率、打印工艺与缺陷容许标准校准。降低阈值提高敏感度，但可能增加误报。

ROI应包含完整条码及左右空白边缘。严重倾斜、透视、裁切应先改善采集或配准。当前检查排除定位主体上下各1像素，不能据此承诺端部全部无缺损。

## SDK

```csharp
var settings = new FieldSettings(
    barcodePrint: new BarcodePrintOptions(
        enabled: true, minimumArea: 4, minimumFraction: .01, edgeTolerance: 1));
var region = new InspectionRegion("barcode", ERegionKind.Barcode, bounds, field: settings);
```

`IBarcodePrintInspector`为独立可替换任务接口，默认实现`OpenCvBarcodePrintInspector`。宿主可以通过`OpenCvInspectionBackend(..., barcodePrint: inspector)`替换打印检查器，而不替换读码器。

质量不足仍将外观NG候选降为REVIEW；已执行的明确码区读取失败NG保留，表示本次不可读，不代表已确认全部物理缺陷。关闭打印检查不关闭条码Expected/任务绑定。

## 已验证与未验证

- 双运行时测试：理想条纹不误报、原始坐标/面积、缺墨/多墨、水平/垂直、阈值、ROI偏移、配置序列化、低质量降级、关闭检查、二维明确不支持、附带文字分离、真实CODE128仍可解码的断条。
- 对用户此前保存的原图及条码ROI回放，两个CODE93仍解码成功；灰度损失增强前参数产生左21、右8处缺墨候选，合计29处，输出对应原图红框与面积。
- 这些候选没有完整人工缺陷标注，不能声称29处全部为真缺陷，不能宣称工业准确率/召回率。
- 自身参考可能把大范围共同缺损当成正常结构；整条缺失、贯穿条高的均匀损坏、绝对条宽、完整静区及编码结构正确性没有全面检查。
- QR已提供模块内部局部墨迹检查，见`QR_PRINT.md`；DataMatrix等其他二维墨迹检查仍未实现，不提供ISO 15416/15415评级。
- 一维任务支持水平/垂直条纹，不提供任意角度/透视校正。QR网格检查的几何限制另见`QR_PRINT.md`。无法形成稳定条纹证据时REVIEW，不以重编码或猜测强行放行。

## 私有回放工具

```powershell
# 输出必须为新目录，原图、旧报告不修改。
dotnet tools/DP.LabelInspection.BarcodeRegression/bin/Release/net8.0-windows/DP.LabelInspection.BarcodeRegression.dll `
  <原图.png> <配方.json> <新输出目录>
```

工具只提取配方中的条码ROI执行当前检查，输出报告、标注图与每个ROI的局部标注PNG。仅用于开发回放，不将ROI之外的区域宣称为已检查。

验证日志与私有图像在`artifacts/barcode-user-final-net48*`、`barcode-user-final-net8*`及`barcode-print-verification.log`，不要未经审查对外发布。
