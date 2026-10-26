"""
Validate u2netp.onnx on the 4 real sample photos before wiring it into the C# app.
u2net's known preprocessing (same as rembg): resize to 320x320, normalize with
mean=[0.485,0.456,0.406] std=[0.229,0.224,0.225], NCHW float32. Output is a 1x1x320x320
saliency mask (sigmoid already applied in this checkpoint); resize back to original size.
"""
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

MODEL = Path(__file__).resolve().parent.parent.parent / "Mobile-LogoExtract" / "LogoExtract" / "Resources" / "Raw" / "Models" / "u2netp.onnx"
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def run(image_path: Path, session: ort.InferenceSession) -> Image.Image:
    img = Image.open(image_path).convert("RGB")
    orig_size = img.size
    small = img.resize((320, 320), Image.BILINEAR)
    arr = np.asarray(small, dtype=np.float32) / 255.0
    arr = (arr - MEAN) / STD
    arr = arr.transpose(2, 0, 1)[None, ...].astype(np.float32)  # 1x3x320x320

    input_name = session.get_inputs()[0].name
    outputs = session.run(None, {input_name: arr})
    mask = outputs[0][0, 0]  # 320x320, already 0..1
    mask = (mask - mask.min()) / (mask.max() - mask.min() + 1e-8)

    mask_img = Image.fromarray((mask * 255).astype(np.uint8)).resize(orig_size, Image.BILINEAR)
    rgba = img.convert("RGBA")
    rgba.putalpha(mask_img)
    return rgba


def main():
    out_dir = Path(__file__).resolve().parent.parent / "outputs"
    out_dir.mkdir(exist_ok=True)
    print(f"Loading {MODEL} ...")
    session = ort.InferenceSession(str(MODEL), providers=["CPUExecutionProvider"])
    print("Inputs:", [(i.name, i.shape) for i in session.get_inputs()])
    print("Outputs:", [(o.name, o.shape) for o in session.get_outputs()])

    samples_dir = Path(__file__).resolve().parent.parent / "samples"
    targets = sys.argv[1:] or sorted(p.name for p in samples_dir.glob("*") if p.suffix.lower() in (".jpg", ".png"))
    for name in targets:
        path = samples_dir / name
        print(f"Running on {path} ...")
        result = run(path, session)
        out_path = out_dir / f"{path.stem}_u2netp_cutout.png"
        result.save(out_path)
        print(f"  -> {out_path}")


if __name__ == "__main__":
    main()
