# 单行OCR移植 · 0.1.0-preview.2（历史记录）

> 本文件记录0.1阶段，不代表当前能力清单。0.2已实现单字分割、比较、字库、条码和桌面工作流，见`FULL_DELIVERY.md`与`VALIDATION.md`。

## 本轮完成

- 新增独立 `DP.LabelInspection.Ocr.Onnx` DLL，双目标net48/net8.0-windows，CPU/x64。
- 真实预处理 → ONNX推理 → CTC解码 → 类型化SDK结果 → WinForms显示，不再只有零张量兼容性探针。
- `ITextLineRecognizer`、`ITextLinePreprocessor`均使用自有图像/几何/数据契约。
- OCR程序集不引用OpenCV、HALCON或WinForms。当前预处理实现位于OpenCV适配器，宿主通过任务接口注入，未来HALCON可提供另一实现。
- CTC保留所有时间步的最高类别与概率，包括blank、连续重复；输出解码激活段。
- 图像块按BGR通道顺序、48像素高度、比例缩放、最小320及最大4096输入宽度、零值右侧padding处理。
- 原图ROI、实际缩放宽度和padding宽度一并保留，激活段**没有伪装成物理字符框**。
- 同一识别器串行执行；会话释放幂等，取消在推理前后及处理阶段响应，不能硬中断原生算子。

## 尚未完成

**图像归属单字分割、单字模板管理/比较、业务文字规则、条码和自动行检测仍待移植。**

- Text ROI必须显式声明`singleLine: true`，不把旧的未声明文字ROI或多行ROI强行送入单行识别。
- 声明为单行是调用方责任，不代表软件已经证明图中恰好一行。
- 与Ignore交叠的文字ROI不识别，避免遮掉像素后仍输出正常完整文字。
- OCR成功不代表字符外观合格。当前文字区域仍返回REVIEW，后端不宣称具备字符分割/模板比较能力。
- 空输出保留为空，置信度0；不注入预期值，不用人工转录纠正结果。
- 整体混合报告仍可因独立固定/空白检查的明确缺陷为NG。

## 模型与可追溯性

已验证模型：现有RapidOCR发行包的`ch_PP-OCRv4_rec_infer.onnx`。

```text
SHA256: 48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b
输出类别数: 6625（含blank与space）
ONNX Runtime: C# 1.28.0；Python参考1.29.0
OpenCV: C# 4.10；Python参考5.0
```

- 从同一份模型字节快照计算SHA256并创建会话，字典来自其内嵌metadata。
- 校验输入/输出类型与维度、字典类别数、输出概率范围与归一性。
- 构造函数可传`expectedSha256`，错误模型拒绝加载。正式宿主建议固定哈希，只加载可信模型。
- 模型最大256MiB，不自动下载、不自动选择未知模型、不包含Python运行依赖。
- 预处理/CTC行为对照RapidOCR/PaddleOCR公开实现；依赖、模型和上游Apache-2.0等许可仍须在正式分发时审查并携带相应声明。没有将模型复制到本项目或宣称其权利归属。

## SDK接入

```csharp
using var recognizer = new OnnxTextLineRecognizer(
    modelPath, new OpenCvTextLinePreprocessor(), expectedSha256: modelHash);
using var backend = new OpenCvInspectionBackend(recognizer); // 借用，不负责释放recognizer
using var engine = new InspectionEngine(backend);

var roi = new InspectionRegion("value", ERegionKind.Text, bounds, singleLine: true);
var recipe = new InspectionRecipe("line", frame.Width, frame.Height,
    EInspectionMode.Free, EAlignmentMode.AssumeAligned, new[] { roi });
var report = await engine.InspectAsync(new InspectionRequest(frame, recipe), token);
var line = report.Analysis.Regions[0].Recognition;
// line.Text、Confidence、Steps、Tokens、Bounds、ModelSha256
// 即使质量门降级，真实识别证据也不会被Core丢失。
```

分别引用Contracts/Core/Vision.OpenCv/Ocr.Onnx对应命名空间。WinForms仍仅接收`IInspectionEngine`，没有加入ONNX或OpenCV引用。

## 打开实际OCR窗体

```powershell
.\start-ocr.cmd
.\start-ocr.cmd net48
```

启动脚本优先尊重已设置的`DP_LABEL_REC_MODEL`；未设置时先寻找`models/rec.onnx`，否则尝试当前工作区原型发行包中的既有ONNX文件。只是读取模型文件，不启动Python或HTTP服务。

也可自行设置模型路径后运行普通`start.cmd`。未配置模型的普通启动保持原有基础检查；配置错误则报错，不悄悄忽略错误继续无OCR运行。

## 实际验证结果

两套CLR分别通过：

| 验证 | 结果 |
|---|---|
| MSTest | 各30通过，0失败/跳过 |
| Release编译 | 0警告、0错误 |
| 48个实拍ROI＋4个合成ROI | 文本52/52与Python参考一致 |
| 全时间步CTC最高类别序列 | 52/52与Python参考一致 |
| 人工转录全文一致 | 51/52；实拍47/48，合成4/4 |
| 窄版冻结M012200015 / Wafer裁图 | 两项均识别一致 |
| 模型哈希不符拒绝、取消、释放后调用 | 通过 |
| WinForms真实模型异步执行 | 两个框架均识别A1020并显示REVIEW |

保留已知`O/0`歧义：frame-4/part仍识别为`WO00001301`，人工转录为`W000001301`。未作静默纠正，也没有把与Python一致称作全部识别正确。

这批ROI是人工选定的可见字段，含建库来源、近重复图和4行合成数据。**这些结果是移植行为回归，不是工业准确率、盲测或缺陷检出率证明。**

## 复现

在本项目目录运行。Python仅用于生成开发参考基线，C#回归执行不需要Python进程：

```powershell
python tools\generate_ocr_baseline.py `
  ..\LabelInspection\samples\production artifacts\ocr-oracle.tsv

.\verify.ps1 `
  -RecognitionModel "$env:DP_LABEL_REC_MODEL" `
  -ProductionAssets "..\LabelInspection\samples\production" `
  -OcrOracle "artifacts\ocr-oracle.tsv"
```

生成器保存逐时间步参考类别、图像哈希及模型哈希sidecar；C#工具检查哈希，**只把图像与ROI输入SDK**，识别完成后才读取参考文本进行比较。

证据：`artifacts/ocr-net48.log`、`ocr-net8.log`、`winforms-net48.png`、`winforms-net8.png`及同名`.png.txt`。日志/图像含生产标识，不应未经审查外发。

下一步：以这些已验证身份假设为输入，移植真实墨迹归属分割，验证邻字污染回归，再接常规/窄体单字模板。
