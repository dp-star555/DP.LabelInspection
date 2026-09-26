"""汇总各方法结果：用同一规则从按图留一得分标定每个字符模型的阈值，统计误报、检出与排序能力，写报告。

阈值 = 规则(该轮该键全部训练字符的留一得分) × --margin。
  --rule max    留一得分最大值（方法B产品的做法，但按整张图留一）
  --rule p90    第90百分位（一个离群良品不会抬高阈值）
--floor group-median：阈值不低于同组各字符阈值的中位数（组内至少5个字符，否则取该轮全部字符的中位数），与产品一致。
方法B另列产品当前的阈值，用于看标定方式的影响：
  dp-b@model    模型自身阈值（按单个字符图留一；同一行重复的字符留一时另一个仍在训练中，阈值偏紧）
  dp-b@product  检测时实际使用的阈值（模型阈值加中位数下限）

输出（前缀由--out-name指定，默认report）：report.md（汇总、缺陷字符逐方法倍数、各方法误报最高的字符）、
report.html（带字符图）、report-per-character.csv（每个测试字符在各方法下的倍数）。

用法：python compare.py <导出目录> [--rule max|p90] [--margin 1.5] [--floor none|group-median]
"""

from __future__ import annotations

import argparse
import csv
import html
import math
import os
from collections import defaultdict

from common import load_index


def read_results(path: str) -> dict[tuple[str, str], dict[str, str]]:
    with open(path, encoding="utf-8-sig", newline="") as f:
        return {(r["run"], r["id"]): r for r in csv.DictReader(f)}


def rule_value(scores: list[float], rule: str) -> float:
    scores = sorted(scores)
    if rule == "max":
        return scores[-1]
    if rule == "p90":
        k = (len(scores) - 1) * 0.9
        lo, hi = math.floor(k), math.ceil(k)
        return scores[lo] + (scores[hi] - scores[lo]) * (k - lo)
    raise ValueError(rule)


def median(values: list[float]) -> float:
    values = sorted(values)
    return values[len(values) // 2]


def auroc(positives: list[float], negatives: list[float]) -> float:
    if not positives or not negatives:
        return float("nan")
    wins = sum((p > n) + 0.5 * (p == n) for p in positives for n in negatives)
    return wins / (len(positives) * len(negatives))


def thresholds(sets, results, rule, margin, floor):
    """(run, key) → 阈值；只有一张训练图含该键时为NaN（无法标定）。"""
    thr: dict[tuple[str, str], float] = {}
    group_of: dict[tuple[str, str], str] = {}
    for ks in sets:
        loo = [float(results[(ks.run, c.id)]["score"]) for c in ks.train if (ks.run, c.id) in results]
        thr[(ks.run, ks.key)] = rule_value(loo, rule) * margin if loo else float("nan")
        group_of[(ks.run, ks.key)] = ks.key.split("/")[0] if "/" in ks.key else ""
    if floor == "group-median":
        by_run: dict[str, list[float]] = defaultdict(list)
        by_group: dict[tuple[str, str], list[float]] = defaultdict(list)
        for (run, key), t in thr.items():
            if not math.isnan(t):
                by_run[run].append(t)
                by_group[(run, group_of[(run, key)])].append(t)
        for (run, key), t in list(thr.items()):
            if math.isnan(t):
                continue
            group = by_group[(run, group_of[(run, key)])]
            ref = median(group) if len(group) >= 5 else (median(by_run[run]) if len(by_run[run]) >= 5 else float("nan"))
            if not math.isnan(ref):
                thr[(run, key)] = max(t, ref)
    return thr


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("data")
    parser.add_argument("--rule", choices=["max", "p90"], default="max")
    parser.add_argument("--margin", type=float, default=1.5)
    parser.add_argument("--floor", choices=["none", "group-median"], default="none")
    parser.add_argument("--out-name", default="report", help="输出文件名前缀，默认report（report.md、report.html）")
    args = parser.parse_args()

    sets = load_index(args.data)
    tests = [(ks, c) for ks in sets for c in ks.test]
    results_dir = os.path.join(args.data, "results")
    methods = sorted(f[:-4] for f in os.listdir(results_dir) if f.endswith(".csv"))
    ratios: dict[str, dict[tuple[str, str], float]] = {}
    for method in methods:
        results = read_results(os.path.join(results_dir, method + ".csv"))
        thr = thresholds(sets, results, args.rule, args.margin, args.floor)
        ratios[method] = {}
        for ks, c in tests:
            r = results.get((c.run, c.id))
            t = thr[(ks.run, ks.key)]
            if r is not None and not math.isnan(t) and t > 0:
                ratios[method][(c.run, c.id)] = float(r["score"]) / t
        if method == "dp-b":
            # 产品当前的阈值：模型自身（按单个字符留一，同一行重复的字符互相作证，阈值偏紧）与加中位数下限后的实际阈值。
            for name, column in (("dp-b@model", "model_threshold"), ("dp-b@product", "product_threshold")):
                ratios[name] = {}
                for ks, c in tests:
                    r = results.get((c.run, c.id))
                    if r is not None and r.get(column):
                        ratios[name][(c.run, c.id)] = float(r["score"]) / float(r[column])
    names = list(ratios)

    rows = []
    for name in names:
        rs = ratios[name]
        good = [rs[(c.run, c.id)] for _, c in tests if c.truth == "good" and (c.run, c.id) in rs]
        bad = [rs[(c.run, c.id)] for _, c in tests if c.truth == "defect" and (c.run, c.id) in rs]
        unmarked = [rs[(c.run, c.id)] for _, c in tests if c.truth == "unmarked" and (c.run, c.id) in rs]
        missing = sum(1 for _, c in tests if (c.run, c.id) not in rs)
        rows.append(
            (
                name,
                f"{sum(r > 1 for r in good)}/{len(good)}",
                f"{sum(r > 1 for r in bad)}/{len(bad)}",
                f"{sum(r > 1 for r in unmarked)}/{len(unmarked)}",
                f"{max(good):.2f}" if good else "-",
                f"{min(bad):.2f}" if bad else "-",
                f"{auroc(bad, good):.3f}" if bad and good else "-",
                str(missing),
            )
        )

    header = ["方法", "良品误报", "缺陷检出", "缺陷图未标注字符报警", "良品最大倍数", "缺陷最小倍数", "AUROC", "无结果"]
    md = [
        "# 逐字符异常检测对比",
        "",
        f"阈值：按整张图留一得分的 **{args.rule}** × {args.margin}，下限 {args.floor}。倍数 = 得分 / 阈值，>1 报警。",
        "“良品最大倍数 < 缺陷最小倍数”表示存在能完全分开的阈值；AUROC 为缺陷与良品字符倍数的排序正确率（1 为完全分开）。",
        "",
        "| " + " | ".join(header) + " |",
        "|" + "---|" * len(header),
    ]
    md += ["| " + " | ".join(r) + " |" for r in rows]

    defects = [(ks, c) for ks, c in tests if c.truth == "defect"]
    md += ["", "## 缺陷字符（倍数）", "", "| 字符 | 键 | " + " | ".join(names) + " |", "|" + "---|" * (len(names) + 2)]
    for ks, c in defects:
        cells = [f"{ratios[n][(c.run, c.id)]:.2f}" if (c.run, c.id) in ratios[n] else "-" for n in names]
        md.append(f"| {c.id} | {ks.key} | " + " | ".join(cells) + " |")

    md += ["", "## 各方法倍数最高的良品字符", ""]
    for name in names:
        worst = sorted(
            ((ratios[name][(c.run, c.id)], c, ks) for ks, c in tests if c.truth == "good" and (c.run, c.id) in ratios[name]),
            key=lambda x: -x[0],
        )[:8]
        md.append(f"- **{name}**：" + "，".join(f"{c.id}（{ks.key}，{r:.2f}）" for r, c, ks in worst))

    with open(os.path.join(args.data, args.out_name + ".md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")

    with open(os.path.join(args.data, args.out_name + "-per-character.csv"), "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["run", "id", "key", "truth", "path"] + names)
        for ks, c in tests:
            w.writerow(
                [c.run, c.id, ks.key, c.truth, c.path]
                + [f"{ratios[n][(c.run, c.id)]:.4f}" if (c.run, c.id) in ratios[n] else "" for n in names]
            )

    write_html(args, header, rows, names, tests, ratios)
    print("\n".join(md[:8 + len(rows)]))
    print(f"\n报告：{os.path.join(args.data, args.out_name)}.md、.html、-per-character.csv")


def write_html(args, header, rows, names, tests, ratios) -> None:
    def cell(r):
        if r is None:
            return "<td>-</td>"
        color = "#c62828" if r > 1 else "#2e7d32"
        return f'<td style="color:{color}">{r:.2f}</td>'

    interesting = [
        (ks, c)
        for ks, c in tests
        if c.truth in ("defect", "unmarked") or any(ratios[n].get((c.run, c.id), 0) > 1 for n in names)
    ]
    order = {"defect": 0, "good": 1, "unmarked": 2}
    interesting.sort(key=lambda x: (order.get(x[1].truth, 3), x[1].run, x[1].id))
    parts = [
        "<!doctype html><meta charset='utf-8'><title>逐字符异常检测对比</title>",
        "<style>body{font-family:sans-serif;margin:16px}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 8px;text-align:right}"
        "img{height:48px;image-rendering:pixelated}th{background:#f3f3f3}</style>",
        f"<h2>逐字符异常检测对比</h2><p>阈值：按图留一得分 {html.escape(args.rule)} × {args.margin}，下限 {html.escape(args.floor)}。</p>",
        "<table><tr>" + "".join(f"<th>{html.escape(h)}</th>" for h in header) + "</tr>",
    ]
    parts += ["<tr>" + "".join(f"<td>{html.escape(v)}</td>" for v in r) + "</tr>" for r in rows]
    parts.append("</table><h3>缺陷字符、缺陷图的其他字符，以及任一方法报警的良品字符</h3><table><tr><th>字符图</th><th>编号</th><th>键</th><th>真值</th>")
    parts.append("".join(f"<th>{html.escape(n)}</th>" for n in names) + "</tr>")
    for ks, c in interesting:
        parts.append(
            f"<tr><td><img src='{html.escape(c.path)}'></td><td>{html.escape(c.run)} {html.escape(c.id)}</td>"
            f"<td>{html.escape(ks.key)}</td><td>{c.truth}</td>" + "".join(cell(ratios[n].get((c.run, c.id))) for n in names) + "</tr>"
        )
    parts.append("</table>")
    with open(os.path.join(args.data, args.out_name + ".html"), "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


if __name__ == "__main__":
    main()
