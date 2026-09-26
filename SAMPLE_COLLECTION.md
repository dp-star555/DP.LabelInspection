# 样本采集与导出（传统算法评估 / 方案B数据准备）

传统算法（分割、单字比较、条码/QR印刷检查）是当前方案；训练式模型作为方案B储备。两者都需要同一份带人工判定的实拍样本：
传统算法用它回放评估每次改动的漏检/误判，方案B用它训练和验收。缺陷样本只在生产中偶然出现，事后无法补采，所以现在就开始保存。

## 宿主接入

检测控件只发出`InspectionCompleted`，不自行保存；由宿主决定保存哪些任务：

1. 检测完成后`InspectionStore.SaveReport(request, report, annotated)`：保存原图、精确配方、报告、字库快照及哈希清单。
2. 操作员复判时追加人工判定，不覆盖算法判定：
   - 整任务：`AddFeedback(jobId, "OK"|"NG"|"REVIEW", comment, author)`；
   - 单个ROI：`AddRegionFeedback(jobId, regionName, "OK"|"NG"|"REVIEW", comment, author)`。放行算法NG的ROI、确认漏检的ROI都应按ROI记录，这是最有价值的标注。
3. 需要时`new SampleExporter(store).Export(新目录, margin)`导出样本集。只读原任务。

建议：NG、REVIEW及人工改判的任务全部保存；OK任务按比例抽样保存，控制磁盘占用。

## 样本集格式（`dp.labelinspection.samples.v1`）

- `crops/<任务>-<序号>.png`：按ROI裁取的原图（含全局对齐偏移，四周留`margin`像素），灰度或彩色与原图一致。
- `index.jsonl`：每行一个样本：
  - `job`、`utc`、`recipe`、`region`、`kind`（Text/Barcode/Fixed/Blank）、`bounds`（裁图在原图中的[x,y,w,h]）；
  - `text`（OCR字符假设）、`expected`（引导值）；
  - `algorithm`（ROI级算法判定OK/NG/REVIEW）、`codes`（非OK发现代码）、`defects`（非OK发现框，裁图坐标）；
  - `segmentation`（分割依据）、`glyphs`（逐字：字符、状态、裁图坐标框、差异、缺墨、多墨）；
  - `humanRegion`（该ROI最近一条人工判定）、`humanJob`（整任务最近一条人工判定）；
  - `label`：训练/评估用标签。优先ROI人工判定；否则整任务人工判定为OK时为OK；整任务NG/REVIEW无法确定是哪个ROI，留空。
- `manifest.json`：格式、导出时间、样本数、`margin`及各标签计数。

没有人工判定的样本`label`为空，不应把算法判定当作真值训练。

## 方案B接入点

模型实现作为现有可替换接口的另一种实现接入，不改配方、流程和报告：

- 文字质量：`DP.Vision.Algorithms.ITextQualityInspector`（`OpenCvInspectionBackend`构造参数`textQuality`）；已有不依赖分割/字库的整段质量策略测试（`IndependentTextQualityTests`）。
- 条码/QR质量：`IBarcodeQualityInspector`。
- 模型运行时按`ALGORITHM_MODULES.md`中拟议的`DP.Vision.Onnx`能力包实现。

输出仍为发现（代码、框、面积、判定），报告不依赖某一实现的内部参数（如二值化阈值），两种方案可用同一样本集按ROI类型比较检出率与误判率。
