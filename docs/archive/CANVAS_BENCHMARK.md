# HALCON原生控件与当前通用画布：同机性能实测

## 结论先行

**当前GDI+验证画布在大图、连续换图、大量XLD点场景明显慢于HALCON HWindowControl；通用数据契约可行，不等于当前渲染实现已经具备原生控件性能。**

但不是HALCON所有项目都赢：10万条碎片Region游程的缓存重绘，GDI+约14.02ms，HALCON约16.80ms。一般4MP纯图约7.23ms / 0.62ms（GDI+ / HALCON）；10万XLD点约33.34ms / 11.46ms。结果高度依赖图像大小、几何形状和缓存/更新策略，不能用单一倍数概括。

## 环境与方法

- 实测时间：2026-09-08；原始JSON有每个进程的UTC时间。
- CPU：Intel Core 7 250H，14核/20逻辑处理器；内存约16GB。
- 系统：Windows 10 10.0.19045；Intel Graphics驱动32.0.101.7076；2560×1600、60Hz。系统另有GameViewer虚拟显示适配器，Session=Console。桌面/远程控制软件可能影响同步时间。
- 两侧均Release、net48、x64，同一1000×700可见控件；HALCON23.11 Progress。
- 原生侧：真正的`HWindowControl`，`DispObj`，`flush=false`＋每帧`FlushBuffer`；不是HALCON算子执行耗时，也不是HighGUI或HSmartWindowControl测试。
- 通用侧：当前产品`ImageViewerControl`＋`CanvasGeometryLayer`，使用缓存Bitmap/GraphicsPath；不是另外写的简化假画布。
- 输入均为人工生成Gray8棋盘图；4MP具体为2544×1608，16MP为4000×4000。Region为单对象中的离散短游程；XLD为20条正弦轮廓。无生产图片、无OCR/检测算法、无相机/磁盘读图。
- 不透明同色Region填充，XLD同色2屏幕像素线宽；同样拟合边距、缩放/平移轨迹。保留各渲染器自身栅格化策略；缩小时Region像素覆盖与抗锯齿不保证逐像素一致。
- 8场景×2控件×2轮＝**32个独立进程**；第二轮反转控件先后顺序。每阶段3次预热；重绘/缩放各50次，适配重绘16次，连续换图20次，提取12次，桌面同步30次。
- 自动校验提取后游程/点数量没有减少；32进程全部通过。截图只取控件缓冲区，不截取整个桌面。
- 下方**P50是两轮各自中位数的均值**，P95是两轮中较差者，不是合并原始样本后的百分位；仅两轮，未给统计置信区间。

### 阶段的准确含义

1. `extract_neutral_geometry`：HALCON游程/轮廓→拥有自己数据的中立DTO，不包括显示、XLD属性与JSON序列化。
2. `cached_redraw_submit`：已有图像与几何的完整重绘，包含窗口缓冲提交及`GdiFlush`。**不是屏幕实际FPS**。
3. `zoom_pan_submit`：更新视图＋重绘。放大后可见范围更小，耗时低于全图重绘不矛盾。
4. `scene_refresh_submit`：源HObject已就绪。通用侧每次重新提取并建立路径后绘制；HALCON直接显示现有原生对象。源几何不变，**不含生成新HObject的算法成本，不代表完整动态算法链路**。
5. `image_stream_submit`：同一Gray8字节输入改变一个像素，每次创建新图像并显示。HALCON包含`GenImage1`复制；通用侧包含新ImageFrame、Bitmap转换以及当前SetImage清空/重装几何缓存的成本。不是采集线程实际吞吐量。
6. `cached_redraw_dwm_sync`：完整重绘＋`DwmFlush`；全部返回0。衡量桌面合成同步等待，不是高速相机测量的输入到光子延迟。60Hz下轻载约16.7ms，两轮均值可能落在两个刷新周期之间，不能反算成真实FPS。
7. 首次显示：创建控件、显示窗口、安装初始图像/几何、首次绘制及DWM同步；HALCON源数据/运行库已准备，**不是进程冷启动或首次许可证验证**。

## 主要实测参数

| 场景 | 原生缓存重绘ms | 通用缓存重绘ms | 原生换图ms | 通用换图ms |
|---|---:|---:|---:|---:|
| VGA | 0.45 | 2.55 | 0.47 | 2.79 |
| 4MP纯图 | 0.62 | 7.23 | 1.16 | 16.28 |
| 16MP纯图 | 0.60 | 20.71 | 3.89 | 61.97 |
| 4MP＋2千游程 | 0.92 | 7.21 | 1.44 | 16.50 |
| 4MP＋10万游程 | 16.80 | 14.02 | 16.55 | 28.63 |
| 4MP＋1万XLD点 | 2.60 | 9.02 | 2.97 | 19.08 |
| 4MP＋10万XLD点 | 11.46 | 33.34 | 11.78 | 43.30 |
| 4MP＋4千游程＋2万点 | 3.98 | 11.88 | 4.47 | 22.92 |

**完整P95、缩放平移、CPU、首次显示、内存与同步表：** [tables.md](artifacts/canvas-benchmark-validated/tables.md)。机器可读汇总：[metrics.csv](artifacts/canvas-benchmark-validated/metrics.csv)。

### 适配数据成本

中立DTO提取P50：2千游程0.21ms；10万游程8.58ms；1万XLD点0.63ms；10万XLD点7.22ms；混合场景1.50ms。10万游程的较差一轮P95为21.76ms，有明显长尾。静态结果应提取/缓存一次，而不是每次拖动画布都重新提取。

### CPU与内存

| 参数 | HALCON | 通用GDI+ |
|---|---:|---:|
| 4MP缓存重绘CPU时间/次 | 0.78ms | 7.34ms |
| 10万XLD点缓存重绘CPU时间/次 | 11.41ms | 33.59ms |
| 4MP加载控件增量Private Bytes | 40.66MiB | 15.11MiB |
| 4MP全流程峰值Working Set，两轮均值 | 65.14MiB | 106.56MiB |
| 16MP全流程峰值Working Set，两轮均值 | 98.50MiB | 365.06MiB |
| 4MP连续20次换图自然Gen2 GC次数，每轮 | 0 | 11 |
| 加载后/测试末尾GDI句柄 | 33 / 33 | 26 / 26 |

- **内存口径特别重要**：为隔离显示增量，两侧测试进程都保留相同HALCON源夹具，因此两侧均加载HALCON。不能把这些进程总量解释成“纯通用画布部署必需内存”。独立不加载HALCON显示已在可行性探针验证，本次不是其基础内存测试。
- 加载增量取控件创建前后差值；Private Bytes含分配器保留量，Working Set为驻留内存，不能混用。全流程峰值也包括图像换帧、提取和一次控件缓冲截图。
- GDI+初始控件增量不总是更大，但连续换图内存峰值及GC压力明显更高。句柄短跑无增长，不等于通过长期泄漏/耐久性测试。
- CPU为进程CPU时间/次，未计GPU与其他系统进程开销。没有GPU显存、GPU执行时间或输入到光子延迟数据。

## 当前瓶颈与下一步判断

1. **大图复制/转换**：`DrawingImageConverter.ToBitmap`会`CopyPixels`并逐行转换/复制到BGR24 Bitmap；当前换图还创建新的ImageFrame。Gray8→BGR24带来额外内存与CPU成本，这与实测连续换帧GC相符。
2. **高点数XLD绘制**：10万点即使已有路径缓存，GDI+重绘仍约33ms；只优化HALCON提取不足以解决。
3. **几何重装**：当前`SetImage`会清空几何，换帧后又建立路径。通用画布工程化时应区分帧身份、几何版本和可安全复用的显示缓存，不能为省开销把旧结果误画到新图。
4. **碎片Region不可简单下结论**：本夹具GDI+缓存重绘略快，但通用侧每次重新提取＋绘制约27.63ms，原生约16.67ms。其他游程形状、透明度、缩放比例仍需测。

建议：**保留中立契约，让渲染器也可替换；不要把通用画布等同于只能使用GDI+。** 当前GDI+适合静态标签复核和中等规模叠加；16MP持续换图、10万点交互不宜直接按当前原型承诺高帧率。下一阶段先做受控缓冲复用/Gray8显示路径、静态几何缓存分离，再以同一基准评估Direct2D或其他GPU渲染器。尚未实现/实测这些优化，不能预报提升倍数。

## 复现与证据

```powershell
# 从DP.LabelInspection目录运行，需要本机兼容HALCON安装及许可。
dotnet build tools/DP.LabelInspection.CanvasBenchmark -c Release
powershell -ExecutionPolicy Bypass -File tools/DP.LabelInspection.CanvasBenchmark/run.ps1 -OutputDirectory <新目录> -Rounds 2
python tools/DP.LabelInspection.CanvasBenchmark/summarize.py <新目录>
```

探针不加入默认解决方案和发行包。Python仅用于离线统计，不参与画布或SDK运行。

- 正式数据：`artifacts/canvas-benchmark-validated/`，32份JSON、控件缓冲PNG、日志、环境信息。
- 全程日志：`artifacts/canvas-benchmark-suite-validated.log`。
- 原生与通用混合场景：[HALCON](artifacts/canvas-benchmark-validated/mixed-halcon-r1.png) / [GDI+](artifacts/canvas-benchmark-validated/mixed-unified-r1.png)。
- 构建：`artifacts/canvas-benchmark-build.log`，Release零警告/错误。
- 本次未修改生产渲染实现，未重发dist；对比仅代表此版本、此机、此负载，不代表未来优化版上限。
