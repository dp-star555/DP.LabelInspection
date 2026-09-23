"""仅供开发生成对照基准，SDK和运行时不调用Python。
使用现有原型虚拟环境运行，对原图和素材只读访问。
"""
import argparse
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np
import rapidocr_onnxruntime
from rapidocr_onnxruntime import RapidOCR

p = argparse.ArgumentParser()
p.add_argument("assets", type=Path)
p.add_argument("output", type=Path)
a = p.parse_args()
model = Path(rapidocr_onnxruntime.__file__).parent / "models/ch_PP-OCRv4_rec_infer.onnx"
rec = RapidOCR(rec_model_path=str(model)).text_rec
manifest = json.loads((a.assets / "manifest.json").read_text(encoding="utf-8"))
lines = []
for case in manifest["cases"]:
    path = a.assets / case["image_file"]
    image = cv2.imread(str(path))
    sha = hashlib.sha256(path.read_bytes()).hexdigest()
    for field in case["fields"]:
        x, y, w, h = field["source_box"]
        crop = image[y:y+h, x:x+w]
        norm = rec.resize_norm_img(crop, max(320/48, w/h))
        prediction = rec.session(norm[np.newaxis].astype(np.float32))[0]
        text = rec.postprocess_op(prediction)[0][0]
        classes = prediction.argmax(axis=2)[0]
        lines.append("\t".join(map(str, [case["id"], field["field"], case["image_file"], x, y, w, h, text,
            field.get("manual_transcription_for_evaluation_only", ""), sha, ",".join(map(str, classes))])))
a.output.parent.mkdir(parents=True, exist_ok=True)
a.output.write_text("\n".join(lines) + "\n", encoding="utf-8")
Path(str(a.output) + ".model-sha256").write_text(hashlib.sha256(model.read_bytes()).hexdigest(), encoding="ascii")
print(f"Wrote {len(lines)} oracle rows to {a.output}; includes synthetic rows separately identified.")
