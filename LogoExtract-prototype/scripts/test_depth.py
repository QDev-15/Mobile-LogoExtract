"""
Step-0 prototype: test whether a generic monocular depth model can perceive fabric wrinkles
well enough to be useful for un-warping a logo printed on wrinkled cloth.

Not fabric-specific (no such pretrained model is readily available) -- this validates whether
a GENERAL depth model (Depth Anything V2) already picks up the shading/parallax cues wrinkles
create, as a first, cheapest check before investing in anything fancier (mesh unwrap, synthetic
fine-tuning, multi-shot fusion, ...).

Usage: python scripts/test_depth.py samples/<image>.jpg
Writes outputs/<image>_depth.png (grayscale depth map) and outputs/<image>_depth_overlay.png
(depth as a color heatmap next to the original, side by side) for visual inspection.
"""
import sys
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from transformers import AutoImageProcessor, AutoModelForDepthEstimation
import matplotlib.pyplot as plt

MODEL_ID = "depth-anything/Depth-Anything-V2-Small-hf"


def main():
    if len(sys.argv) not in (2, 3):
        print("Usage: python scripts/test_depth.py <path-to-image> [model-id]")
        sys.exit(1)

    image_path = Path(sys.argv[1])
    global MODEL_ID
    if len(sys.argv) == 3:
        MODEL_ID = sys.argv[2]
    out_dir = Path(__file__).resolve().parent.parent / "outputs"
    out_dir.mkdir(exist_ok=True)

    print(f"Loading {MODEL_ID} (first run downloads the weights, ~100 MB)...")
    processor = AutoImageProcessor.from_pretrained(MODEL_ID)
    model = AutoModelForDepthEstimation.from_pretrained(MODEL_ID)
    model.eval()

    image = Image.open(image_path).convert("RGB")
    print(f"Image: {image.size[0]}x{image.size[1]}")

    inputs = processor(images=image, return_tensors="pt")
    with torch.no_grad():
        outputs = model(**inputs)
        predicted_depth = outputs.predicted_depth

    # Resize depth prediction back to the original image size.
    prediction = torch.nn.functional.interpolate(
        predicted_depth.unsqueeze(1),
        size=image.size[::-1],
        mode="bicubic",
        align_corners=False,
    ).squeeze().numpy()

    # Normalize to 0..1 for visualization (relative depth only -- Depth Anything V2's
    # base checkpoints give relative, not metric, depth: good enough to see the fold
    # geometry, not to measure real-world distances).
    depth_norm = (prediction - prediction.min()) / (prediction.max() - prediction.min() + 1e-8)

    model_tag = MODEL_ID.split("/")[-1].replace("Depth-Anything-V2-", "").replace("-hf", "")
    stem = f"{image_path.stem}_{model_tag}"
    gray_path = out_dir / f"{stem}_depth.png"
    Image.fromarray((depth_norm * 255).astype(np.uint8)).save(gray_path)
    print(f"Saved grayscale depth map -> {gray_path}")

    fig, axes = plt.subplots(1, 3, figsize=(15, 5))
    axes[0].imshow(image)
    axes[0].set_title("Original")
    axes[0].axis("off")

    im = axes[1].imshow(depth_norm, cmap="turbo")
    axes[1].set_title("Depth (relative, turbo colormap)")
    axes[1].axis("off")
    fig.colorbar(im, ax=axes[1], fraction=0.046, pad=0.04)

    # A crude "surface normal from depth gradient" preview: shows which direction each
    # patch of fabric is tilting, the thing the actual unwarp step would need next.
    gy, gx = np.gradient(depth_norm)
    normal_preview = np.dstack([
        0.5 + gx * 2,
        0.5 + gy * 2,
        np.ones_like(gx) * 0.8,
    ])
    normal_preview = np.clip(normal_preview, 0, 1)
    axes[2].imshow(normal_preview)
    axes[2].set_title("Crude surface-tilt preview (from depth gradient)")
    axes[2].axis("off")

    overlay_path = out_dir / f"{stem}_depth_overlay.png"
    fig.tight_layout()
    fig.savefig(overlay_path, dpi=150)
    print(f"Saved side-by-side comparison -> {overlay_path}")
    print("\nOpen the overlay PNG and check: do visible wrinkles/folds in the original")
    print("show up as corresponding ridges/valleys in the depth map and tilt preview?")


if __name__ == "__main__":
    main()
