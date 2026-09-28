# HALCON Region/XLD → 中立数据 → 当前画布：可行性实测

## 结论

**可行，且已调用本机真实HALCON做过验证，不是仅根据接口名推断。**

环境：本机`HALCON 23.11 Progress / x64-win64`，使用安装目录`bin/dotnet35/halcondotnet.dll`；提取探针运行于net48。中立显示分别运行于net48和net8.0-windows，未加载HALCON模块。

可行的是“几何数据与显示解耦”。不是宣称已经实现完整HALCON对象模型、所有XLD子类型、编辑器、生产级插件热切换或高帧率显示。

## 实际验证链路

1. 用HALCON生成Region：带孔矩形和远处分离圆形属于同一Region，另有单像素Region、空Region，共3个对象。
2. 用HALCON生成开放/闭合亚像素轮廓及真实`EdgesSubPix`边缘轮廓。
3. `WriteObject`保存`.hobj`，`ReadObject`重新加载，再逐对象提取；不是自研解析其私有二进制布局。
4. Region提取227条游程，转换为中立半开区间，再重建HALCON Region。每个对象与原Region做`SymmDifference`，面积均为0；总像素面积22139。
5. XLD提取3条轮廓、228个点；从中立double坐标重建，点序列逐值相等。开放/闭合状态保留。
6. 边缘轮廓还提取了`angle`、`response`、`edge_direction`，每项221个逐点值。示例没有全局属性，不能据此声称已测试所有全局属性类型。
7. 释放全部源HObject后，中立快照仍可序列化和使用。
8. 独立WinForms进程读取`geometry.json`，调用当前`ImageViewerControl.SetGeometry`绘制真实几何层；检查填充区域、白色孔洞、分离岛、亚像素折线及缩放/平移。
9. 显示进程检查托管程序集和本机模块，均无HALCON。不是使用HALCON窗口，也不是把HALCON窗口截图贴到画布。

验证图：

- `artifacts/halcon-geometry-extract-v2/geometry.json.fit.png`
- `artifacts/halcon-geometry-extract-v2/geometry.json.zoom-pan.png`
- `.render.txt`记录无HALCON加载的显示验证。

## 中立表示与准确性

| 源数据 | 中立几何 | 已保留/注意事项 |
|---|---|---|
| Region | `CanvasRegion`＋`CanvasRun` | 按对象保存，保留孔洞、断开部分、空对象、单像素；不能用外接矩形替代 |
| XLD contour | `CanvasPolyline`＋double `CanvasPoint` | 点序、亚像素、开闭标记；不先取整，不强制所有轮廓闭合 |
| XLD逐点属性 | 探针中的独立JSON属性记录 | 已读取3种真实属性；统一属性schema与完整回写尚未加入产品 |
| XLD polygon等子类型 | 需要对应提取器 | 官方还有`GetPolygonXld`（坐标、段长、法线角）、`GetParallelsXld`等；本次实际显示链路只验证XLD contour |

HALCON的`ColumnEnd`包含结束像素，中立游程用`EndColumnExclusive=ColumnEnd+1`。`GetRegionRuns`每次只接收一个Region，要先按对象集合`CountObj/SelectObj`遍历，不能直接把集合拉平成一片区域。

XLD的Row/Column转换为Y/X。中立点采用“整数坐标是像素中心”，而栅格像素格从整数边界开始，所以画布绘制轮廓时显式处理半像素偏移。源double精度保留，GDI+屏幕路径使用float，不代表显示设备具备无限亚像素精度。

HALCON生成Region时还可能受`clip_region`及当前图像域影响。探针在独立进程中关闭生成裁切以构造完整样例；**正式适配器不应偷偷修改宿主的HALCON全局设置**。提取只能保留已经存在的数据，不能补回上游裁切掉的部分。

## 对通用上位画布的意义

推荐Seam：

```
HALCON Adapter ─┐
OpenCV Adapter ─┼─→ 中立图像/几何/结果快照 ─→ 画布Renderer
其他算法Adapter ┘                           ├→ 选择/交互
                                           └→ 存储/导出
```

主画布不应直接接收`HObject`、`Mat`，也不应该根据算法品牌决定显示逻辑。算法适配器负责原点、ROI局部偏移、坐标系、对象集合、内存所有权和属性映射；画布负责图层、样式、视图变换与交互。

必须明确的Interface规则：

- 原图坐标与局部ROI坐标不能混用；裁图后的原点偏移要显式携带。
- 输入帧与结果快照要有同一帧/周期身份，防止异步旧结果画到新图。
- 编辑ROI属于配置；算法结果几何属于只读证据，不应混成同一个可变对象。
- 几何可跨算法显示，不等于算法定义、置信度、标定信息、原生算子语义完全等价。
- 原生文件读取/数据提取仍需要对应HALCON版本和许可；中立数据保存后，纯显示不再需要HALCON。

## 当前实现范围

已加小型验证底座：

- Contracts：`CanvasGeometry.cs`，不依赖HALCON/GDI+。
- WinForms：`SetGeometry`与`CanvasGeometryLayer`，复用现有缩放/平移变换；切换图像清除旧几何，释放缓存路径。
- 可选提取探针：`tools/DP.LabelInspection.HalconGeometryProbe`，**不加入默认解决方案/发行包**，不让普通构建或运行依赖HALCON许可。

仍待工程化：非矩形图层拾取/编辑、显示与证据ID关联、完整XLD属性模型、其他XLD子类型、WPF同等渲染、帧同步/增量刷新、海量Region和百万点性能。当前缓存GraphicsPath只证明可用，不是生产性能承诺。后续已完成同机原生/通用对比，实测参数见[CANVAS_BENCHMARK.md](CANVAS_BENCHMARK.md)；大图、连续换帧及高点数XLD确实存在明显差距。

## 复现命令

```powershell
$env:PATH="$env:HALCONROOT\bin\$env:HALCONARCH;"+$env:PATH
dotnet build tools/DP.LabelInspection.HalconGeometryProbe -c Release
# 必须用新输出目录
& tools/DP.LabelInspection.HalconGeometryProbe/bin/Release/net48/DP.LabelInspection.HalconGeometryProbe.exe <新输出目录>

# 独立显示：不引用HALCON，不需要加载模型
& samples/DP.LabelInspection.Demo.WinForms/bin/Release/net48/DP.LabelInspection.Demo.WinForms.exe --geometry-probe <新输出目录>/geometry.json
dotnet samples/DP.LabelInspection.Demo.WinForms/bin/Release/net8.0-windows/DP.LabelInspection.Demo.WinForms.dll --geometry-probe <新输出目录>/geometry.json
```

官方依据来自本机`$HALCONROOT/doc/html/reference/operators/`下的`get_region_runs.html`、`get_contour_xld.html`、`get_polygon_xld.html`以及安装版.NET XML文档。没有解析或导出本机许可证内容。
