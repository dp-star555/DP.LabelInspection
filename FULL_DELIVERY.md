# 功能整合交付 · 0.2.0-preview.1

## 交付定义

按已确认的“x64、双框架、WinForms优先、SDK/算子解耦、基本检测保留”连续实施，本次交付的是**完整可操作的WinForms检测链路＋无UI SDK＋原生WPF基础控件**。不再以只有接口、占位返回或ONNX零张量探针代替功能。

“完整链路”不等于所有扩展目标已经实现：HALCON、WPF全部编辑界面同等功能、相机/PLC/MES、可靠热卸载和工业精度认证均未交付。

## 功能落地范围

### 检测
- 固定Key和可变Value分离、有参考/无参考、质量门、固定差分、空白污点、忽略区。
- 固定内容上的受限ECC平移；失败不套用失效坐标，结果位置回到待检原图坐标。
- PP-OCRv4真实单行识别，模型字典/哈希、动态宽度、CTC blank和重复处理。
- 可选PP-OCRv4 DB真实检测，提出保守水平文字矩形；无ROI时探索文字/条码，必须人工核对覆盖。
- 空隙/连通域归属单字分割：不强切真正粘连；保留自身笔画及未归属小噪点；抑制已知邻字墨迹。
- 以实际拥有的字符Patch做112×112归一比较，不重新裁相交外接框。
- 字符类别、固定修订、字符标签字典检索，支持乱序和重复；常规/窄体共享生产库。
- 内容预期、全匹配正则、字符集、长度；显式等宽单元模式。
- 新增明确ROI交叉绑定、每次请求任务字段绑定、周期/有效期校验及数据快照追溯，见`DATA_BINDING.md`。后台取数由宿主实现，不内置特定MES连接。
- ZXing实际1D/QR等解码＋独立一维条/空隙局部缺墨、多墨检查，原图面积与缺陷框；另有QR模块内部缺墨/多墨及固定结构整模块翻转检查，以及可选4模块静区墨迹检查；不声称完整编码结构、DataMatrix外观或ISO等级。见`BARCODE_PRINT.md`、`QR_PRINT.md`。

### 单字资产与追溯
- 新建空类别；单字符加载/裁剪、确认、替换、删除；规则字表导入只是一次性输入独立字形。
- 不可变修订、过期编辑拒绝、并发发布互斥、归档/恢复、历史版本读取。
- Python `labelscope.glyph-library.v1`导入/导出，PNG原字节哈希、大小、二值化方式及来源字段保留。
- 导入新ID；配方绑定不跟随“最新版本”自动变化；种子不重置用户已有版本。
- 报告包含原图、可选参考、配方、检测结果、标注图、精确字库快照及SHA256清单。
- 追加人工反馈，不覆盖机器结论；导出前验证报告哈希，ZIP以新文件原子发布。

### 桌面操作
- WinForms主控件、独立图像控件、可嵌入字库控件均只直接依赖Contracts。
- 完整宿主：载图、模型加载、ROI坐标/规则编辑、类别版本绑定、阈值、配方、批量、自动保存、历史、人工复核与ZIP。
- 字符画廊展示原始单字、参考、归一实际、差异；可以下载或确认补库。
- WPF为真正原生Canvas/BitmapSource控件：图像、ROI鼠标框选、异步检测、证据及字符图；复用同一SDK，不通过WinFormsHost嵌套。

## 实测摘要

- Release全解决方案：0警告、0错误。
- net48 / net8：各170项MSTest通过，0失败/跳过。
- 各运行时52行回归：48行实拍＋4行合成，识别文本及全时间步CTC类别均与Python参考一致。
- 可选外观检查：未绑定字库的普通文字ROI仅执行OCR/内容校验，不再进行分割或报缺库。
- 当前375个分割字符＝339实拍＋36合成；370次比较＝334实拍＋36合成；已绑定库缺字实例5个。
- 历史482字符基线中的107个未绑定字库候选，现在不请求分割；没有通过放宽阈值消除缺陷。
- 保留示例回归集中唯一的超阈值候选；没有通过提高阈值隐藏它。
- 冻结`M012200015`裁图的已知邻字墨迹残留为0，`Wafer`分割也通过。
- 无ROI模型探索只用于配方准备评估；候选数量和解码结果不等同于正式验收。
- 两套WinForms均验证鼠标ROI、真实OCR、36字组合比较、字库鼠标裁剪、报告/ZIP；两套WPF实际启动并完成NG示例。

**统计是移植与行为回归，不是缺陷准确率。** 人工全文一致为51/52，其中实拍47/48；保留`O/0`歧义。原图有近重复和建库来源，合成图也不计作独立实拍样本。

## 运行与交付目录

- 源工程：`DP.LabelInspection.sln`。
- 主控件：`src/DP.LabelInspection/bin/Release/<target>/DP.LabelInspection.dll`。
- WPF控件：`src/DP.LabelInspection.Wpf/bin/Release/<target>/DP.LabelInspection.Wpf.dll`。
- 完整WinForms：`start-ocr.cmd`或`start-ocr.cmd net48`。
- WPF示例：`start-wpf.cmd`或`start-wpf.cmd net48`。
- 发布：`package.ps1`，默认输出`dist/0.2.0-preview.1/<target>/<WinForms|Wpf>/`。

发布默认**不带模型、生产图片或用户数据**。明确提供模型目录时可以打包模型；正式分发前必须检查模型、OpenCV、ONNX、ZXing等许可和本机原生运行库。

```powershell
.\package.ps1 -ModelDirectory "C:\trusted-models\ppocr" `
  -OutputDirectory "dist/local-with-models"
```

每套目录带启动脚本、实际DLL/原生依赖和部署说明，根目录有SHA256SUMS。框架依赖部署，net48需要.NET Framework 4.8，net8需要.NET 8 Desktop Runtime。不是一个DLL覆盖所有运行时，也不是本机之外所有环境都已验证。

## 验证复现

开发参考生成器只用于产生测试基线，C# SDK及回归执行均不调用Python：

```powershell
python tools\generate_ocr_baseline.py `
  ..\LabelInspection\samples\production artifacts\ocr-oracle.tsv

.\verify.ps1 `
  -RecognitionModel "$env:DP_LABEL_REC_MODEL" `
  -ProductionAssets "..\LabelInspection\samples\production" `
  -OcrOracle "artifacts\ocr-oracle.tsv"
```

基线旁的`.model-sha256`必须保留。每张输入PNG和模型均核验哈希；参考文字只在SDK执行后用于比较，不作为识别输入。

主要证据位于`artifacts/`：`full-verification.log`、`ocr-net48.log`、`ocr-net8.log`、`winforms-*.png`、`.production.png`、`.glyphs.png`、`.library.png`和`wpf-*.png`。

## 接入约定与明确边界

- 宿主拥有引擎/模型；控件不私建全局引擎，也不擅自释放宿主资源。
- 创建控件的UI线程负责设置；算法在SDK工作线程运行，输入、参数和字库修订都是快照。
- 取消是合作式，不能硬杀原生算子；关闭前异步等待任务，避免在UI线程Wait/Result。
- SDK持久化方法为同步任务，工作台在后台执行报告保存；宿主应按自己的线程策略调度。
- 框架没有现成宿主插件基类，因此入口是普通DLL接口，不虚构MEF/宿主协议。
- `ICharacterSegmenter`、`IGlyphComparer`也能单独注入替换，复用OCR/字库/业务流程；默认是OpenCV实现，不等于已提供HALCON实现。
- 原生库版本冲突、HALCON许可/版本选择、热卸载/进程隔离仍需真实宿主条件，当前没有保证。
- 单行水平/ASCII字符分割、归一尺寸与试验阈值限制仍存在；不保证绝对印刷尺寸、缺整行或未知字符的业务真值。
- WPF编辑器UI不与WinForms全等；所需业务操作可通过同一SDK调用，不能把它描述成完整双UI工作台。
- `OCR_MIGRATION.md`为0.1阶段历史记录；当前能力以本文件、README和VALIDATION为准。
