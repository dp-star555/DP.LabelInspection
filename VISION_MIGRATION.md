# Vision 单图像体系迁移（进行中，不可据此宣布发布完成）

## 最终目标（不保留两套检测图像接口）

- 正式检测输入及运行中图像：`DP.Vision.ImageFrame`（身份及租约）与 `IImageSource`（借用像素），宿主/请求明确释放租约。
- 配方及结果：只持久化需要长期保存的图像字节；运行中借用图像和持久化快照不是同一种对象。不能把已释放的 `IImageSource` 保存在报告里。
- 算法：通用定位、仿射、读码、质量测量使用 `DP.Vision.Algorithms` 的接口和实现；LabelInspection只决定检查范围、读取/质量门控、版本及业务判定。
- ROI：局部几何经矩阵映射为当前帧原图掩膜；需摆正时单独生成图像副本，任何测量证据反向映回原图。
- 完成迁移时删除 `DP.LabelInspection.Contracts.ImageFrame` 及其运行时依赖、剩余码质检/自定义策略的旧图像委托接口和纯转换代码。`IBarcodeDecoder`、标签侧 OCR/文本候选/字符分割/单字比较通用接口与Runtime转发类均已删除。测试里的快照构造辅助仅用于验证仍需持久化的标签侧证据，不作为产品适配器。

## 实际进度

1. 已具备Vision侧可逆仿射矩阵、精确ROI轮廓/掩膜、按掩膜读码和独立摆正预览。
2. 检测报告可携带帧身份、可逆矩阵；**正式定位目前只产生平移**，未输入变换时使用恒等矩阵（不是全零矩阵）。
3. `InspectionRequest.FromVision` 能保留帧租约；生产读码只接收Vision `IBarcodeReader`，部分OCR、质量检测与异常检测也直接使用Vision源。仍有旧算法按需复制为标签侧快照，绝不宣称全流程零复制。
4. WinForms/WPF示例已改用Vision OCR和Vision读码器；OCR回归工具改用Vision ONNX识别器、预处理及Vision请求（冻结分割测试仍使用标签快照）。两个工作台现在各自Retain输入图像并用 `InspectionRequest.FromVision` 创建运行请求；调用方可在设置输入后释放原始句柄。旧快照仍用于预览和训练/存储，UI尚未完全去除旧图像类型。WinForms公开的 `CreateRequest()` 由调用方释放；工作台持有的最近请求仅借用至下次修改/运行/关闭。WPF完成事件的请求仅可同步消费，下次检测/关闭后失效。
5. 真实Vision帧 + Vision OCR 的正式文字读取/内容核对已验证；文字A规则质检复用Vision租约。Vision原图上的码印刷质量、固定/空白裁图质检、ECC平移定位以及整ROI/逐字符B检测已有直接路径；旧式自定义策略、字库训练资产、工作台与存储仍使用旧快照。
6. 生产读码入口只接受 `DP.Vision.Algorithms.IBarcodeReader`；标签侧 `IBarcodeDecoder`、Runtime的ZXing委托类已删除。
7. 样本导出对带矩阵的新报告核对与旧整数偏移的一致性，拒绝旋转/缩放/剪切，避免生成错误的训练裁图；没有矩阵的历史报告继续按旧平移字段导出。WinForms/WPF 工作台遇到非整数平移矩阵时隐藏轴对齐ROI叠加，WinForms也拒绝用偏移反算编辑坐标；目前尚未实现仿射ROI的直接交互显示。
8. OCR预处理和CTC回归改为直接测Vision实现；删除标签侧OCR模型/预处理委托、旧 `ITextLineRecognizer`、仅供适配器使用的输入类型及重复CTC解码委托。生产OCR只注入Vision `ITextLineRecognizer`；生产文字质检及逐字异常分割/单字比较也只注入Vision `ICharacterSegmenter` / `IGlyphComparer`，已删除 `LegacySegmenter` / `LegacyComparer` 的反向图像复制适配。Core无论采用分阶段还是旧后台，配方/参考/ROI校验均只读Vision元数据，不因验证而复制像素。候选提取服务/后台现只接受借用的Vision `IImageSource`，异步入口在调度前独立Retain、结束后释放，OCR与分割无需再复制整帧；UI及训练存量旧快照会在调用边界转成Vision源。文本候选定位器由制作工具直接用Vision实现；删除标签侧 `ITextRegionDetector` / `TextRegionDetector`，未调用定位器的正式检测后台不再虚报Discovery能力。旧 `ICharacterSegmenter` / `IGlyphCandidateSegmenter` / `IGlyphComparer` 及Runtime转发类均已删除；仍需长期保存的字符图块和报告证据才复制为标签侧像素快照。其他旧图像策略仍在。

9. 算子下沉到DP.Vision（需与DP.Vision分支 `claude/vision-operators` 一起合并）：逐字符异常检测（字符归一化、逐字局部块比较、缺墨检查、多样性选样、热力图）改为Vision `ICharacterAnomalyDetector` / `OpenCvCharacterAnomalyDetector`，标签侧只保留字符组键、库条目、组内阈值下限和报告措辞；受限ECC平移改为Vision `ITranslationRegistrar` / `OpenCvTranslationRegistrar`，忽略区掩码由 `InspectionMask.Compose` 生成（固定/空白质检掩码图用 `InspectionMask.ToImage`）；批量训练样本框对齐改为注入Vision `ITemplateLocator`。删除 `CharacterCells` / `CharacterLine` / `CharacterCell` / `CharacterInkLoss` / `TranslationRegistration`、后台 `Gray` 副本、训练会话手写NCC与灰度缓存、无调用的标签侧码质检接口（`IBarcodePrintInspector` 等）及Runtime包装类。实拍标签回归转储与迁移前逐字节一致；ECC在随机平移/忽略区上与旧实现逐次一致。

10. 类型统一：标签侧 `PixelRect` 结构已删除，改为 DP.Vision `PixelBounds` 的全局别名（`Directory.Build.props` 的 `<Using Alias="PixelRect">`），源码写法与持久化JSON（`[x,y,w,h]`）不变；`AlgorithmContractAdapter` 中矩形互转删除，调用处直接传递。外部宿主若引用 `DP.LabelInspection.Contracts.PixelRect` 全名，需改为 `DP.Vision.Algorithms.PixelBounds` 或加同样的别名。`BarcodePrintOptions` 同样改为Vision类型的全局别名（逐字段一致）。`BarcodeObservation` / `BarcodeModuleGrid` 暂不统一：标签侧网格角点为像素中心坐标、Vision为像素边缘坐标（相差0.5），直接替换会使QR模块网格及已保存报告中的网格偏移半个像素，须先定义报告迁移。

## 尚未完成（阻止删除旧类型/发布标准化SDK）

- 将Core的剩余图像任务、码质量、异常模型训练/检测等接口逐项迁入Vision租约，不让运行中任务访问旧 `ImageFrame`；字库候选后台已直接使用Vision，制作UI和训练库存量旧快照仍需转换。
- 字库、异常模型、候选库和报告保存使用独立持久化像素表示；明确哈希、版本和释放责任，历史JSON/PNG兼容回归必须通过。
- 补齐WinForms/WPF工作台异步工作、关闭/取消、预览、输入帧身份的自动化测试；进一步移除UI预览及工具内部的标签侧图像快照，避免运行时常驻两份像素。
- 样本导出及两套UI不能再只依据 `OffsetX/OffsetY` 切框或画框；非平移矩阵未完整支持前明确阻断，避免导出/展示伪证据。
- 切换所有测试、示例、工具的调用，再移除余下旧类型和冗余适配，执行双框架构建与完整回归。最后检查生产源码中旧 `ImageFrame` 运行时接口、标签侧读码接口/实现等引用数为零。

## 不可妥协的约束

- 不在未完成实际算法的情况下把矩阵数据口当作旋转/仿射定位功能。
- 不把ZXing读出等同工业印刷质量、ISO等级或HALCON读码已交付。
- 不以外接矩形代替斜ROI做精确检测；不静默沿用上帧定位、失效帧或越界图像。
- 迁移过程中先维持可运行的正式行为；**最终交付前移除所有并行旧入口**，不得把过渡实现当正式发布。
