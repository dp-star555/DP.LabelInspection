"""汇总独立新进程中的测量结果；Python仅作为离线分析工具。"""
import csv
import json
import statistics
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
root = Path(sys.argv[1])
records = [json.loads(p.read_text(encoding="utf-8")) for p in root.glob("*-r*.json")]
assert len(records) == 32 and all(r["geometryCountValidated"] and r["dwmStatus"] == 0 for r in records)
cases = ["image_vga", "image_4mp", "image_16mp", "region_2k", "region_100k", "xld_10k", "xld_100k", "mixed"]
labels = ["640×480纯图", "2544×1608纯图", "4000×4000纯图", "4MP＋2千游程", "4MP＋10万游程", "4MP＋1万XLD点", "4MP＋10万XLD点", "4MP＋4千游程＋2万点"]

def samples(case, backend):
    result = [r for r in records if r["workload"] == case and r["backend"] == backend]
    assert len(result) == 2
    return result

def metric(case, backend, name, key="medianMs", maximum=False):
    values = [next(m for m in r["metrics"] if m["name"] == name)[key] for r in samples(case, backend)]
    return (max if maximum else statistics.mean)(values)

with (root / "metrics.csv").open("w", newline="", encoding="utf-8-sig") as f:
    fields = ["workload", "backend", "phase", "medianMeanMs", "worstRunP95Ms", "meanMs", "cpuMsPerOperation", "gen0Mean", "gen2Mean"]
    writer = csv.DictWriter(f, fieldnames=fields)
    writer.writeheader()
    for case in cases:
        for backend in ("halcon", "unified"):
            for name in [m["name"] for m in samples(case, backend)[0]["metrics"]]:
                writer.writerow(dict(workload=case, backend=backend, phase=name, medianMeanMs=metric(case, backend, name), worstRunP95Ms=metric(case, backend, name, "p95Ms", True), meanMs=metric(case, backend, name, "meanMs"), cpuMsPerOperation=metric(case, backend, name, "cpuMsPerOperation"), gen0Mean=metric(case, backend, name, "gen0"), gen2Mean=metric(case, backend, name, "gen2")))

lines = ["## 缓存重绘（毫秒）", "", "| 场景 | HALCON P50 / P95 | GDI+ P50 / P95 | GDI/HALCON P50 |", "|---|---:|---:|---:|"]
for case, label in zip(cases, labels):
    values = [metric(case, b, "cached_redraw_submit") for b in ("halcon", "unified")]
    tails = [metric(case, b, "cached_redraw_submit", "p95Ms", True) for b in ("halcon", "unified")]
    lines.append(f"| {label} | {values[0]:.2f} / {tails[0]:.2f} | {values[1]:.2f} / {tails[1]:.2f} | {values[1]/values[0]:.2f}× |")
lines += ["", "## 操作分解（毫秒，HALCON / GDI+）", "", "| 场景 | 缩放平移提交 | 重复适配＋重绘 | 换图＋重绘 | 重绘＋DWM同步 |", "|---|---:|---:|---:|---:|"]
for case, label in zip(cases, labels):
    cells = [" / ".join(f"{metric(case,b,n):.2f}" for b in ("halcon", "unified")) for n in ("zoom_pan_submit", "scene_refresh_submit", "image_stream_submit", "cached_redraw_dwm_sync")]
    lines.append("| " + " | ".join([label] + cells) + " |")
lines += ["", "## 中立几何提取（毫秒，不含绘制）", "", "| 数据量 | 平均P50 | 较差一轮P95 |", "|---|---:|---:|"]
for case, label in zip(cases[3:], labels[3:]):
    lines.append(f"| {label} | {metric(case,'unified','extract_neutral_geometry'):.2f} | {metric(case,'unified','extract_neutral_geometry','p95Ms',True):.2f} |")
lines += ["", "## 首次显示、CPU、内存（HALCON / GDI+）", "", "| 场景 | 首次显示均值ms | 重绘CPU ms/次 | 控件加载增量Private MiB | 两轮峰值Working Set均值 MiB |", "|---|---:|---:|---:|---:|"]
for case, label in zip(cases, labels):
    cells = []
    for measure in (lambda r: r["initialDisplayAndDwmMs"], lambda r: next(m for m in r["metrics"] if m["name"] == "cached_redraw_submit")["cpuMsPerOperation"], lambda r: r["loaded"]["privateMiB"]-r["beforeWindow"]["privateMiB"], lambda r: r["beforeGc"]["peakWorkingSetMiB"]):
        cells.append(" / ".join(f"{statistics.mean(measure(r) for r in samples(case,b)):.2f}" for b in ("halcon", "unified")))
    lines.append("| " + " | ".join([label] + cells) + " |")
(root / "tables.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
print("\n".join(lines))
