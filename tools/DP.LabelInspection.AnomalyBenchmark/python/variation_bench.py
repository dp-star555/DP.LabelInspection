"""变差模型（variation model）的numpy实现，与HALCON train_variation_model的'standard'模式同一判据，用作参考与交叉检查。

训练：各良品字符图逐像素的均值与标准差。检测：像素偏差比 r = |I − 均值| / max(标准差, --floor)，
可先做 --smooth×--smooth 均值平滑；测试图在 ±--shift 像素内平移取最小的最大比值（相当于检测前的精定位）。
字符得分 = 最大比值；HALCON中等价于 prepare_variation_model(AbsThreshold=floor·V, VarThreshold=V) 时恰好开始报缺陷的V。

用法：python variation_bench.py <导出目录> [--floor 8] [--smooth 3] [--shift 1]
"""

from __future__ import annotations

import argparse
import time

import numpy as np

from common import ResultWriter, leave_one_image_out, load_index, read_gray


def box(image: np.ndarray, size: int) -> np.ndarray:
    if size <= 1:
        return image
    import cv2

    # HALCON的mean_image在边界处镜像，与BORDER_REFLECT接近（边界像素可能有微小差别）。
    return cv2.blur(image, (size, size), borderType=cv2.BORDER_REFLECT)


class VariationModel:
    """与AnomalyBenchmark.Halcon相同的计算：测试图按±shift平移裁出与均值图内部同尺寸的区域，逐位置比较后取最小。"""

    def __init__(self, images: list[np.ndarray], floor: float, smooth: int, shift: int):
        stack = np.stack([i.astype(np.float32) for i in images])
        self.mean = stack.mean(axis=0)
        self.std = np.maximum(stack.std(axis=0), floor)
        self.smooth = smooth
        self.shift = shift

    def score(self, image: np.ndarray) -> float:
        test = image.astype(np.float32)
        h, w = test.shape
        s = self.shift
        mean = self.mean[s : h - s, s : w - s]
        std = self.std[s : h - s, s : w - s]
        best = np.inf
        for dy in range(-s, s + 1):
            for dx in range(-s, s + 1):
                moved = test[s + dy : h - s + dy, s + dx : w - s + dx]
                best = min(best, float(box(np.abs(moved - mean) / std, self.smooth).max()))
        return best


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("data")
    parser.add_argument("--floor", type=float, default=8.0, help="标准差下限（灰度级），默认8")
    parser.add_argument("--smooth", type=int, default=3, help="偏差比均值平滑窗口，默认3（1为不平滑）")
    parser.add_argument("--shift", type=int, default=1, help="测试图平移搜索半径（像素），默认1")
    parser.add_argument("--name", default="variation-numpy")
    args = parser.parse_args()

    start = time.time()
    sets = load_index(args.data)
    cache: dict[str, np.ndarray] = {}

    def gray(cell):
        if cell.path not in cache:
            cache[cell.path] = read_gray(args.data, cell)
        return cache[cell.path]

    def model(cells):
        return VariationModel([gray(c) for c in cells], args.floor, args.smooth, args.shift)

    n = 0
    with ResultWriter(args.data, args.name) as out:
        for ks in sets:
            if ks.test:
                m = model(ks.train)
                for c in ks.test:
                    out.write(c, m.score(gray(c)))
                    n += 1
            for _, rest, held in leave_one_image_out(ks):
                m = model(rest)
                for c in held:
                    out.write(c, m.score(gray(c)))
                    n += 1
    print(f"{args.name}: {n} scores in {time.time() - start:.1f} s -> {out.path}")


if __name__ == "__main__":
    main()
