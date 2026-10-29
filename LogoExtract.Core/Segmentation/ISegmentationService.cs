using SkiaSharp;

namespace LogoExtract.Core.Segmentation;

/// <summary>Which ONNX Runtime execution provider <see cref="U2NetSegmentationService"/> should build its
/// session with. Owner's request, 2026-10-29: "setting dùng 1 trong 2... máy cpu mạnh thì dùng only cpu, còn
/// lại thì dùng nnapi/gpu" -- on-device A/B testing that same day found NNAPI ~2.4x SLOWER than CPU-only on one
/// real device (see <see cref="U2NetSegmentationService"/>'s own constructor comment for the actual numbers),
/// which is why this is a per-device CHOICE (benchmarked and cached by
/// <c>LogoExtract.Services.SegmentationProviderResolver</c>, overridable in Settings) rather than one or the
/// other hardcoded -- NNAPI driver quality varies enough by SoC vendor that neither option is safe to assume
/// universally faster.</summary>
public enum SegmentationProvider
{
    Cpu,
    Nnapi,
}

/// <summary>Background-removal result: an alpha mask the same size as the input image, 0 = fully transparent
/// (background), 255 = fully opaque (foreground). The caller composites this onto the original RGB to get the
/// final RGBA cutout -- kept separate from "apply the mask" so the manual brush/lasso touch-up tool (PLAN.md
/// 2.2) can edit this exact mask before compositing, not just the already-flattened RGBA result.</summary>
public sealed record SegmentationResult(SKBitmap AlphaMask);

/// <summary>Automatic first pass at "which part of this photo is the foreground" (PLAN.md 2.2). This is a
/// generic salient-object model (u2netp), NOT a logo-vs-fabric segmenter and NOT point-prompted: tested
/// 2026-10-27 on 4 real sample photos (LogoExtract-prototype/outputs/*_u2netp_cutout.png) -- works well on
/// flat-lay/mockup product photos (shirt silhouette cut cleanly from a plain or colored background), but does
/// almost nothing useful on a photo of a model WEARING the shirt against a light studio background (no strong
/// contrast for a generic saliency model to key off). The manual brush/lasso tool is therefore the PRIMARY way
/// users reliably get a clean cutout, not a rarely-needed fallback -- do not design the UI as if the automatic
/// suggestion will usually be enough on its own.</summary>
public interface ISegmentationService
{
    SegmentationResult Segment(SKBitmap image);
}
