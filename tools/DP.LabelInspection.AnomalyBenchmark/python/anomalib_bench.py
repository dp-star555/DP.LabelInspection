"""用anomalib的PatchCore与PaDiM模型（torch实现）对同一批字符图评分，结果与导出器、变差模型同一格式。

每个模型键（“组/字符”）单独建模：
- PatchCore：训练字符的块特征组成记忆库（字符少，默认不做核心集抽样，即精确最近邻），图像得分用anomalib的
  compute_anomaly_score（含邻域重加权）。
- PaDiM：逐位置多元高斯（与anomalib MultiVariateGaussian相同的均值/协方差，批量计算），图像得分用anomalib的马氏距离图最大值。
特征只提取一次（骨干网络前向，按字符图内容缓存，各轮中相同的字符图不重复计算），留一时只重建记忆库/高斯。
字符图为灰度，复制成3通道、缩放到 --size×--size，按ImageNet均值方差归一化。

依赖：pip install -r requirements.txt（anomalib 2.x、torch）。首次运行会下载预训练骨干网络（timm/HuggingFace）；
无法联网时可先在有网的机器上运行一次，或设置 HF_HUB_OFFLINE=1 并把权重放入缓存目录。

用法：python anomalib_bench.py <导出目录> [--methods patchcore,padim] [--size 224] [--device auto]
"""

from __future__ import annotations

import argparse
import time

import numpy as np
import torch
import torch.nn.functional as F

from common import ResultWriter, leave_one_image_out, load_index, read_gray

MEAN = torch.tensor([0.485, 0.456, 0.406]).view(1, 3, 1, 1)
STD = torch.tensor([0.229, 0.224, 0.225]).view(1, 3, 1, 1)


def to_tensor(images: list[np.ndarray], size: int, device: torch.device) -> torch.Tensor:
    x = torch.from_numpy(np.stack(images)).float().div(255).unsqueeze(1).repeat(1, 3, 1, 1)
    x = F.interpolate(x, size=(size, size), mode="bilinear", align_corners=False)
    return ((x - MEAN) / STD).to(device)


class PatchCoreScorer:
    name = "anomalib-patchcore"

    def __init__(self, args, device):
        from anomalib.models.image.patchcore.torch_model import PatchcoreModel

        self.model = PatchcoreModel(
            layers=args.patchcore_layers.split(","),
            backbone=args.patchcore_backbone,
            pre_trained=not args.no_pretrained,
            num_neighbors=args.neighbors,
        ).to(device)
        self.coreset = args.coreset
        self.device = device

    @torch.no_grad()
    def embed(self, x: torch.Tensor) -> list[torch.Tensor]:
        """每张图的块特征（行 = 块）。"""
        self.model.train()
        rows = self.model(x)
        self.model.embedding_store.clear()
        return list(rows.view(x.shape[0], -1, rows.shape[-1]))

    @torch.no_grad()
    def score(self, memory: list[torch.Tensor], x: torch.Tensor) -> list[float]:
        bank = torch.cat(memory)
        if 0 < self.coreset < 1:
            from anomalib.models.components import KCenterGreedy

            bank = KCenterGreedy(embedding=bank, sampling_ratio=self.coreset).sample_coreset()
        self.model.memory_bank = bank
        self.model.eval()
        return self.model(x).pred_score.flatten().tolist()


class PadimScorer:
    name = "anomalib-padim"

    def __init__(self, args, device):
        from anomalib.models.image.padim.torch_model import PadimModel

        torch.manual_seed(0)  # PaDiM随机选取特征维度，固定种子便于复现
        self.model = PadimModel(
            backbone=args.padim_backbone,
            layers=args.padim_layers.split(","),
            pre_trained=not args.no_pretrained,
        ).to(device)
        self.device = device

    @torch.no_grad()
    def embed(self, x: torch.Tensor) -> list[torch.Tensor]:
        self.model.train()
        e = self.model(x)
        self.model.memory_bank = []
        return list(e)

    @torch.no_grad()
    def score(self, memory: list[torch.Tensor], x: torch.Tensor) -> list[float]:
        self.model.gaussian.mean, self.model.gaussian.inv_covariance = fit_gaussian(torch.stack(memory))
        self.model.eval()
        return self.model(x).pred_score.flatten().tolist()


def fit_gaussian(embedding: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """与anomalib MultiVariateGaussian.fit相同的均值与逆协方差（样本协方差 + 0.01·I，再 + 1e-5·I 后求逆），
    按位置批量计算；anomalib逐位置循环，留一时要反复拟合，太慢。只有1个样本时协方差取0（anomalib此时除以0）。"""
    batch, channel, height, width = embedding.shape
    vectors = embedding.reshape(batch, channel, height * width)
    mean = vectors.mean(dim=0)
    centered = vectors - mean
    covariance = torch.einsum("nci,ndi->icd", centered, centered) / max(batch - 1, 1)
    identity = torch.eye(channel, device=embedding.device)
    covariance = covariance + 0.01 * identity + 1e-5 * identity
    return mean, torch.linalg.inv(covariance)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("data")
    parser.add_argument("--methods", default="patchcore,padim")
    parser.add_argument("--size", type=int, default=224, help="输入尺寸，默认224")
    parser.add_argument("--device", default="auto", help="auto、cpu或cuda")
    parser.add_argument("--patchcore-backbone", default="wide_resnet50_2")
    parser.add_argument("--patchcore-layers", default="layer2,layer3")
    parser.add_argument("--neighbors", type=int, default=9, help="PatchCore重加权的近邻数，默认9")
    parser.add_argument("--coreset", type=float, default=1.0, help="PatchCore核心集比例，默认1（不抽样）")
    parser.add_argument("--padim-backbone", default="resnet18")
    parser.add_argument("--padim-layers", default="layer1,layer2,layer3")
    parser.add_argument("--no-pretrained", action="store_true", help="随机权重，仅用于离线检查流程，结果无意义")
    args = parser.parse_args()

    device = torch.device("cuda" if args.device == "auto" and torch.cuda.is_available() else
                          ("cpu" if args.device == "auto" else args.device))
    sets = load_index(args.data)
    scorers = {"patchcore": PatchCoreScorer, "padim": PadimScorer}
    for method in args.methods.split(","):
        start = time.time()
        scorer = scorers[method.strip()](args, device)
        name = scorer.name + ("-random" if args.no_pretrained else "")
        n = 0
        cache: dict[bytes, torch.Tensor] = {}
        with ResultWriter(args.data, name) as out:
            for ks in sets:
                cells = ks.train + ks.test
                grays = [read_gray(args.data, c) for c in cells]
                x = to_tensor(grays, args.size, device)
                keys = [g.tobytes() + bytes(str(g.shape), "ascii") for g in grays]
                todo = [i for i, k in enumerate(keys) if k not in cache]
                for i in range(0, len(todo), 32):
                    batch = todo[i : i + 32]
                    for j, e in zip(batch, scorer.embed(x[batch])):
                        cache[keys[j]] = e
                features = {c.id: cache[k] for c, k in zip(cells, keys)}
                index = {c.id: i for i, c in enumerate(cells)}
                if ks.test:
                    scores = scorer.score([features[c.id] for c in ks.train], x[[index[c.id] for c in ks.test]])
                    for c, s in zip(ks.test, scores):
                        out.write(c, s)
                        n += 1
                for _, rest, held in leave_one_image_out(ks):
                    scores = scorer.score([features[c.id] for c in rest], x[[index[c.id] for c in held]])
                    for c, s in zip(held, scores):
                        out.write(c, s)
                        n += 1
        print(f"{name}: {n} scores in {time.time() - start:.1f} s ({device}) -> {out.path}")


if __name__ == "__main__":
    main()
