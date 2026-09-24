# 标签工程功能目录

统一规范和枚举改名表见[文件与类型规范](../DP.Vision/STRUCTURE.md)，每个类型的入口见[源码索引](SOURCE_INDEX.md)。

## 目录职责

| 工程 | 功能目录及职责 |
|---|---|
| `DP.LabelInspection.Contracts` | `Imaging`图像契约；`Geometry`原图几何；`Inspection/Configuration`配方与项目；`Inspection/Execution`引擎和阶段契约；`Inspection/Results`报告与证据；`Rules`内容约束；`Libraries`独立字库；`Libraries/Candidates`参考制作；`Codes`码数据与质量；`Text`识别、检测、分割与质量 |
| `DP.LabelInspection.Core` | `Inspection`引擎、逐ROI调度及完成策略；`Rules`引导约束；`Libraries/Drafting`候选编辑与跨图暂存；`Text/Recognition`解码委托入口 |
| `DP.LabelInspection.Runtime` | `Inspection`组装和ROI会话；`Registration`定位；`Imaging`编解码；`Codes`码读取与质量适配；`Text`字符分割、比较、识别和检测；`Surfaces`固定及空白质量组合 |
| `DP.LabelInspection.Storage` | `Persistence`持久化入口，其私有序列化辅助类型独立存放在`Internal/InspectionStore` |
| `DP.LabelInspection.Adapter.Vision` | `Algorithms`算法契约转换；`Display`图像与显示几何转换 |
| `DP.LabelInspection` | `Workbench`工作台及完成事件；`Workbench/Layout`工作台侧栏、视图工具条及判定徽标等内部布局控件；`Canvas`图像视图及交互事件；`Configuration`ROI与绑定编辑；`Configuration/Converters`中文属性转换；`Libraries`字库管理；`Libraries/Drafting`快速制作；`Codes`码证据视图；`Imaging`位图转换；`Localization`资源访问；`Resources`原资源文件 |
| `DP.LabelInspection.Wpf` | `Workbench`原生WPF工作台及事件 |

## 约定

- 类、接口、结构体、枚举分别存放，文件名对应类型名。
- 私有嵌套类型通过独立`partial`文件保留原作用域，不提高可见性。
- 所有自定义枚举采用`E`前缀，例如`ERegionKind`、`EInspectionVerdict`、`ERoiStageState`。
- 枚举成员和数值不变，类型名已变化；外部宿主必须修改类型引用并重新编译，不提供旧类型转发。
- 命名空间、项目依赖和资源键保持不变，物理目录只承担功能分类。
- 测试按功能归类，测试替身留在所属测试类型的`TestDoubles`目录，不混入生产代码。

完整双框架回归记录见[VALIDATION.md](VALIDATION.md)。
