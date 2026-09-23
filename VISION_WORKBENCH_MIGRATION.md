# 旧标签工作台接入 DP.Vision

## 当前已落地，而非仅提供适配器

### WinForms

公开类型`DP.LabelInspection.ImageViewerControl`保留，内部绘制已改为`DP.Vision.Winform.VisionCanvasControl.RenderTo(Graphics)`：

- 原图复制到独立、只读的`MemoryImageSource`；Gray8仍是Gray8，不再为叠加标记把整幅灰度图扩成BGR Bitmap。
- 图像由通用图块缓存绘制；Region/XLD由通用几何层绘制；ROI轮廓、单字框、检测框由Adapter生成独立readonly图层。
- 保留原标签标题排版、拖动预览和控制点绘制，以及整数矩形ROI的创建/移动/缩放事件。它们仍属于标签UI，不进入通用Core。
- 同一原图更新报告/叠加时只增加Sequence，保持FrameId，不重新复制原图。
- 控件拥有自己的源租约/缓存，释放或清空时释放；不接管检测引擎。

这是真正更换绘制后端，不是把新的画布放在另一个Demo里。以下现有使用点无需改公开调用：

1. 标签工作台主图。
2. 条码单个标记详情窗格（没有新增重复原图窗格）。
3. 字库图像查看和参考裁取。

`SetImage/SetOverlays/SetGeometry/SetCharacters`、`RegionDrawn/RegionEdited/FindingSelected`、放大缩小/适应/1:1等入口保留。鼠标坐标仍由原有统一视图换算，绘制端同步使用该视图。实际显示scale限制与DP.Vision一致：1/1024到128。

### 原生WPF

`DP.LabelInspection.Wpf.LabelInspectionControl`主图已经嵌入真正的`DP.Vision.WPF.VisionCanvasControl`。外部Canvas只承担布局和矩形拖动预览；不再逐次创建主图BitmapSource和大量证据Rectangle控件。主图与readonly证据使用通用源/图层/缓存，鼠标换算读取通用Viewport。

没有WindowsFormsHost。单字小缩略图仍采用原来的WPF BitmapSource，这不等于主图仍走旧管线。

WPF控件现在实现`IDisposable`。宿主应先`await CancelAndWaitAsync()`，再在Dispatcher上Dispose。示例窗口已接入释放流程；Unloaded仅取消操作，不永久销毁可重新挂载的控件。

## 为什么没有直接把全部ROI菜单换成通用编辑器

标签配方当前的`InspectionRegion`是**整数矩形**，并带有字段类型、字库、条码类型、绑定等业务配置。此次保留这些事件和约束，不把圆/旋转框/任意轮廓静默压成外接矩形写回配方。

因此：**绘制后端已替换；标签配方模型和ROI业务编辑逻辑未重写。** 通用Demo的任意形状编辑菜单不会自动出现在旧标签工作台。

## 验证

- Label SDK：net48 / net8各133项通过；DP.Vision各60项通过，均零失败/跳过，构建零警告/错误。
- 标签真实OpenCV、OCR、单字外观、发现、字库及原有WinForms ROI/报告工作流通过。
- WinForms窗口消息回归继续检查滚轮锚点、平移、检测框拾取、ROI创建与移动、适应及1:1；增加实际工作台已使用DP.Vision、Gray8源保持、图块缓存非空的断言。
- 将先前HALCON真实提取的中立几何复制到独立验证目录，在迁移后的公开ImageViewerControl上重跑两框架检查：Region孔洞/分离岛、XLD、缩放平移通过，显示进程不加载HALCON。证据见`artifacts/migrated-geometry-net48/`、`artifacts/migrated-geometry-net8/`，未覆盖原历史基准文件。
- 原生WPF烟测确认实际工作台使用DP.Vision.WPF、主图保留Gray8且图块缓存非空；完成NG报告渲染。
- WPF物理鼠标路由/捕获仍未在本机验证通过，不能用渲染烟测代替该结论。
- 日志：`../DP.Vision/artifacts/workbench-verification-final.log`、`workbench-core-verification.log`。

宿主可通过`RenderingBackend`查看后端；具体查看器/WPF控件还提供`DisplayPixelLayout`、`CachedDisplayPixelBytes`。缓存数字只表示记账的图块像素，不是进程总内存。

## 性能基准与限制

- 历史全BGR/GDI基准冻结在`tools/DP.LabelInspection.CanvasBenchmark/Baseline/ImageViewerControl.cs`与`Baseline/CanvasGeometryLayer.cs`；`unified`模式显式使用冻结类，避免误把已迁移的公开ImageViewerControl当作旧版基准。
- 本轮仅构建验证该可选工具，没有重跑40进程性能矩阵，也没有新的工作台FPS结论。
- 通用图块Renderer的既有性能结果不能直接等同于含OCR/报告/字库的完整工作台吞吐。
- 兼容图像输入仍会复制，尚未接入相机池/异步大文件解码；Label `ImageFrame`原有12000边长/1600万像素限制没有改变。
- 老公共类型继续存在，第三方宿主不用一次性改接口；新部署必须携带对应DP.Vision及Adapter程序集。

## 启动当前版本

```text
DP.LabelInspection/start-ocr.cmd
DP.LabelInspection/start.cmd
DP.LabelInspection/start-wpf.cmd
```

工作台工具栏显示`画布：DP.Vision`。这些脚本构建/使用当前源码；**旧dist目录尚未重发**，运行旧发布目录里的EXE仍然是旧程序。生产图片、报告和字库未加入发布包。
