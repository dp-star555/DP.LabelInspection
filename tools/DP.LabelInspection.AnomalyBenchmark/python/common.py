"""对比评估的公共部分：读取导出器写出的index.csv与字符图，按轮次/模型键组织，写统一格式的结果。

结果文件 results/<方法>.csv 的列：run,id,score。
- 测试字符（index中split=test）：用该轮全部训练字符建模后的得分；
- 训练字符（split=train）：按整张图留一的得分——该图的全部字符不参与建模。
阈值由 compare.py 按同一规则从留一得分统一标定，所有方法相同。
"""

from __future__ import annotations

import csv
import os
from collections import defaultdict
from dataclasses import dataclass, field

import numpy as np


@dataclass
class Cell:
    id: str
    run: str
    key: str
    split: str
    truth: str
    image: str
    path: str


@dataclass
class KeySet:
    """一轮中一个模型键（“组/字符”）的全部字符图。"""

    run: str
    key: str
    train: list[Cell] = field(default_factory=list)
    test: list[Cell] = field(default_factory=list)

    def by_image(self) -> dict[str, list[Cell]]:
        groups: dict[str, list[Cell]] = defaultdict(list)
        for c in self.train:
            groups[c.image].append(c)
        return groups


def load_index(data: str) -> list[KeySet]:
    sets: dict[tuple[str, str], KeySet] = {}
    with open(os.path.join(data, "index.csv"), encoding="utf-8-sig", newline="") as f:
        for row in csv.DictReader(f):
            cell = Cell(row["id"], row["run"], row["key"], row["split"], row["truth"], row["image"], row["path"])
            ks = sets.setdefault((cell.run, cell.key), KeySet(cell.run, cell.key))
            (ks.train if cell.split == "train" else ks.test).append(cell)
    return list(sets.values())


def read_gray(data: str, cell: Cell) -> np.ndarray:
    import cv2

    raw = np.fromfile(os.path.join(data, cell.path), dtype=np.uint8)
    image = cv2.imdecode(raw, cv2.IMREAD_GRAYSCALE)
    if image is None:
        raise IOError(f"cannot read {cell.path}")
    return image


def leave_one_image_out(ks: KeySet):
    """(留出图名, 其余图的训练字符, 留出图的字符)；只有一张图含此键时无法标定，不返回。"""
    groups = ks.by_image()
    if len(groups) < 2:
        return
    for image, held in groups.items():
        rest = [c for g, cells in groups.items() if g != image for c in cells]
        yield image, rest, held


class ResultWriter:
    def __init__(self, data: str, method: str):
        os.makedirs(os.path.join(data, "results"), exist_ok=True)
        self.path = os.path.join(data, "results", f"{method}.csv")
        self._file = open(self.path, "w", encoding="utf-8-sig", newline="")
        self._writer = csv.writer(self._file)
        self._writer.writerow(["run", "id", "score"])

    def write(self, cell: Cell, score: float) -> None:
        self._writer.writerow([cell.run, cell.id, repr(float(score))])

    def close(self) -> None:
        self._file.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
