# 逐字符异常检测对比评估

在同一批字符图、同一阈值规则下比较：

| 方法 | 结果文件 | 说明 |
|---|---|---|
| 方法B（本项目） | `results/dp-b.csv` | 手工特征、位置相关±1像素的局部块最近邻（导出器直接调用产品代码） |
| 变差模型（numpy） | `results/variation-numpy.csv` | 逐像素均值/标准差，与HALCON `train_variation_model` 'standard' 同一判据，作参考与交叉检查 |
| HALCON变差模型 | `results/halcon-variation.csv` | 全部用HALCON算子计算（`DP.LabelInspection.AnomalyBenchmark.Halcon`） |
| anomalib PatchCore | `results/anomalib-patchcore.csv` | WideResNet50 layer2+3，记忆库最近邻，anomalib的图像得分（含重加权） |
| anomalib PaDiM | `results/anomalib-padim.csv` | ResNet18 layer1–3，逐位置多元高斯 |

所有方法都用导出器写出的**同一批归一化字符图**（按行几何缩放到大写高32像素、按基线对齐，与方法B训练时完全相同），
每个“组/字符”单独建模。

## 阈值：统一按整张图留一标定

每个方法都输出两类得分：测试字符的得分，以及训练字符的**按整张图留一**得分（该图全部字符不参与建模时的得分）。
`compare.py` 用同一规则从留一得分定阈值：`规则(留一得分) × 1.5`，规则可选 `max`（默认）或 `p90`，
可加与产品相同的组中位数下限（`--floor group-median`）。倍数 = 得分 / 阈值，大于1报警。

按整张图而不是按单个字符图留一：同一行里重复的字符（“3P1100B”的两个“0”）来自同一次印刷，几乎一样，
留出一个时另一个仍在训练中，留一得分接近0、阈值被压得过紧。产品当前的方法B就是按单个字符图留一，
报告中的 `dp-b@model` 是这种阈值，`dp-b@product` 是加了中位数下限后检测时实际用的阈值，用来看标定方式的影响。

## 准备配置

`bench.json`（路径相对于配置文件所在目录）：

```json
{
  "imageRoot": "images",
  "reference": "good01.jpg",
  "lines": [
    { "name": "MS", "box": [388, 415, 210, 36], "text": "3P1100B" },
    { "name": "SERIAL", "box": [1149, 665, 527, 33], "text": "" },
    { "name": "WF", "box": [113, 676, 259, 32], "text": "WF675907", "group": "WF" }
  ],
  "images": [
    { "file": "good01.jpg", "role": "train", "texts": { "SERIAL": "12605350C7A010167" } },
    { "file": "good02.jpg", "role": "train", "texts": { "SERIAL": "12605350C7A010368" } },
    { "file": "good03.jpg", "role": "good" },
    { "file": "bad01.jpg", "role": "defect", "defects": { "MS": [5, 6] } }
  ],
  "leaveOneOut": true
}
```

- `lines`：文字ROI（参考图坐标）与默认文本；`group`缺省为行名称（每行一组，与批量训练默认一致），同字体的行可填同一组。
  `text`为空的行只在`texts`里给了文本的图上评估（如每张不同的序列号）。
- `reference`：设置时各图先整体配准到参考图，再逐行在±12像素内微调；不设置则按原坐标取行（图已对齐时）。
- `images[].role`：`train` 训练良品；`good` 不参与训练的良品（统计误报）；`defect` 有缺陷的标签。
- `defects`：缺陷图中有缺陷的字符，行名称 → 位序（从1开始，与检测结果“第n位”一致）。未标注的字符记为 unmarked，
  单独统计、不算误报。**缺陷字符标得越全，检出率与AUROC越可信。**
- `leaveOneOut`：训练良品至少3张时，每张轮流不参与训练、作为良品测试（统计误报的主要来源，良品少时尤其重要）。
- 另有 `localRadius`、`thresholdMargin`、`maximumSamples`（方法B参数，默认1、1.5、16）。

## 运行

```powershell
# 1. Python环境（一次）：
python -m venv .venv; .\.venv\Scripts\Activate.ps1
pip install -r tools\DP.LabelInspection.AnomalyBenchmark\python\requirements.txt
#    GPU可选，按 https://pytorch.org 选择对应的torch安装命令；首次运行anomalib会下载预训练骨干网络。

# 2. 先用合成标签检查整条流程（CPU上anomalib约15–20分钟，GPU上约1分钟；只查流程可加 -SkipAnomalib）：
.\tools\DP.LabelInspection.AnomalyBenchmark\run.ps1 -Synthetic -Out D:\bench-synth

# 3. 实拍标签：
.\tools\DP.LabelInspection.AnomalyBenchmark\run.ps1 -Config D:\labels\bench.json -Out D:\bench-out
```

anomalib在CPU上较慢（合成标签1288个字符：PatchCore约6分钟、PaDiM约11分钟，主要是PaDiM每次留一都要对每个特征位置求逆协方差），
有GPU时会自动使用；也可 `-AnomalibArgs "--size 128"` 缩小输入（更快，特征更粗）。
`-SkipAnomalib`、`-SkipHalcon` 跳过对应方法；`-AnomalibArgs "--size 128 --methods patchcore"` 传参数给anomalib脚本。
HALCON部分需要环境变量 `HALCONROOT`（与仓库中其他HALCON工具相同，引用 `bin/dotnet35/halcondotnet.dll`）。

也可以分步运行：

```powershell
dotnet run --project tools\DP.LabelInspection.AnomalyBenchmark -c Release -f net8.0-windows -- export bench.json D:\bench-out
python tools\DP.LabelInspection.AnomalyBenchmark\python\variation_bench.py D:\bench-out
python tools\DP.LabelInspection.AnomalyBenchmark\python\anomalib_bench.py D:\bench-out
dotnet run --project tools\DP.LabelInspection.AnomalyBenchmark.Halcon -c Release -- D:\bench-out
python tools\DP.LabelInspection.AnomalyBenchmark\python\compare.py D:\bench-out [--rule p90] [--floor group-median]
```

## 输出

- `report.html`：汇总表，以及缺陷字符、缺陷图其他字符、任一方法报警的良品字符的字符图与各方法倍数（红色>1）。
- `report.md`：汇总、缺陷字符逐方法倍数、各方法倍数最高的良品字符。`report-p90.*`、`report-floor.*` 为另两种阈值规则。
- `report-per-character.csv`：每个测试字符在各方法下的倍数，可用Excel自行筛选。
- `index.csv`、`cells/`：导出的字符图（`cells/<轮次>/<组>~U<字符编码>/train|test/`），也可直接拿去给其他工具用。

汇总表各列：良品误报（倍数>1的良品字符数）、缺陷检出、缺陷图未标注字符报警、良品最大倍数与缺陷最小倍数
（前者小于后者表示存在能完全分开的阈值）、AUROC（缺陷与良品字符倍数的排序正确率，与阈值规则无关，1为完全分开）。

## 注意

- 结果只对这批标签有代表性；缺陷样本少时，检出率的差别可能只是一两个字符。以AUROC、倍数分布和字符图为主要依据。
- 方法B每个字符最多用16个训练样本（多样性选取），其他方法用全部训练字符；样本不超过16个时没有差别。
- anomalib的方法按原论文的设计是用于几十上百张同一产品的图；这里每个字符只有几个到十几个样本，是在本项目的条件下比较。
- 合成标签只用于检查流程是否跑通，不能代替实拍评估。
- HALCON部分在编写环境中没有HALCON，只按HALCON .NET接口签名编译检查过，未实际运行；
  如果某个算子报错，请把错误信息发给我。它与 `variation-numpy` 计算相同，两者的得分应基本一致（边界平滑与标准差定义可能有微小差别），
  可以用来互相核对。
