# 放置：由宿主定位驱动的 ROI（平移／旋转／缩放）

## 背景

配方 ROI 是配方坐标下的水平矩形。原先配方坐标等于相机原图坐标，标签在图中移动、旋转后 ROI 无法跟随；SDK 自带的对齐只有“假定已对齐”和“整图平移配准”。作为流程节点使用时，定位应复用宿主已有的模板匹配、坐标系等能力，而不是在 SDK 内重做一套。

## 契约

- `InspectionPlacement`：配方坐标 → 本帧原图坐标的仿射（`x' = M11·x + M12·y + Tx`，`y' = M21·x + M22·y + Ty`），连续像素坐标，像素 (i, j) 覆盖 [i, i+1)×[j, j+1)。必须有限且可逆，支持平移、旋转、缩放（含剪切时也能取样）。
- `InspectionRequest.FromVision(..., placement)`：提供放置时，请求只对每个 ROI（外扩 `InspectionRequest.PlacementMargin` = 16 像素）按放置从原图双线性取样，放入配方尺寸的画布；其余像素为 0，不变换整张图。整数像素平移时直接拷贝像素，不插值。
- 检测、定位、质量、识别、读码、异常检测仍在配方坐标下进行，ROI 处理代码不变。`ImageWidth/ImageHeight` 为配方尺寸；`OriginalWidth/OriginalHeight`、`CreateOriginalSnapshot()` 提供放置前的原图；`Placement` 供显示时把报告坐标换算回原图。
- ROI 放置后必须完整落在原图内，否则请求创建失败（`placed_roi_outside_image`）。
- `InspectionPlacement.Rectify(frame, width, height)`：把整个标签区域摆正为配方尺寸，用于配方制作、参考图和显示；生产检测不需要。

## 使用约定

- 参考图（模板模式）必须是配方坐标下的标签图，即用同一放置 `Rectify` 得到的图。
- 放置时推荐 `EAlignmentMode.AssumeAligned`；`Translation` 仍可用于边距范围内的残余偏移。
- 报告中的 ROI、证据、字块坐标均为配方坐标。
- 双线性重采样对逐像素差异和清晰度类指标有轻微影响，建议放置的缩放接近 1。

## 验证

`tests/DP.LabelInspection.Tests/Configuration/InspectionPlacementTests.cs`：整数平移直接取像素、边距外不取样、90° 旋转摆正、ROI 越界拒绝、奇异矩阵拒绝。
