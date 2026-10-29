using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Which technique <see cref="CutoutCompositor.WriteCutoutAndThumb"/> uses to fix the "viền trắng"
/// (white fringe) edge artifact -- owner's request, 2026-10-29: a per-device CHOICE (resolved/persisted by
/// <c>LogoExtract.Services.EdgeTreatmentResolver</c> in the app project, overridable in Settings), same shape as
/// <see cref="Segmentation.SegmentationProvider"/>'s own two-option setting, since the two techniques have a
/// genuine tradeoff (see <see cref="ColorDecontamination"/>'s and <see cref="AlphaChoke"/>'s own doc comments)
/// rather than one being strictly better.</summary>
public enum EdgeTreatment
{
    Decontaminate,
    Choke,
}

/// <summary>Shared mask.png/cutout.png/thumb.png writer -- originally inlined in SegmentViewModel.SaveAsync,
/// pulled out 2026-10-28 so ItemsViewModel's dashboard status-override buttons (Imported -&gt; "hoàn thành",
/// which fabricates a full-keep mask instead of waiting on the model) can produce the exact same on-disk shape
/// without duplicating the pixel-composite loop.</summary>
public static class CutoutCompositor
{
    // Named once here instead of as a repeated "cutout.png"/"thumb.png" literal at every call site
    // (ItemStore.MarkSegmented included) -- the on-disk file names and the ItemState transition that claims
    // they exist must never drift apart.
    public const string MaskFileName = "mask.png";
    public const string CutoutFileName = "cutout.png";
    public const string ThumbFileName = "thumb.png";

    public static void WriteMask(SKBitmap mask, string folder)
    {
        using SKData maskData = mask.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream maskStream = File.Create(Path.Combine(folder, MaskFileName));
        maskData.SaveTo(maskStream);
    }

    /// <summary>Writes cutout.png (edge-treated composite of <paramref name="image"/> + <paramref name="mask"/>'s
    /// R channel as alpha, see <see cref="EdgeTreatment"/>) and a 320px-wide thumb.png derived from it.</summary>
    public static void WriteCutoutAndThumb(SKBitmap image, SKBitmap mask, string folder, EdgeTreatment edgeTreatment = EdgeTreatment.Decontaminate)
    {
        // Exactly one of these two runs, matching `edgeTreatment` -- owner's report, 2026-10-27, "viền trắng
        // nhìn ảo quá", originally fixed with decontamination alone; a second technique (Choke) was added
        // 2026-10-29 as a per-device/per-owner CHOICE once it became clear neither is strictly better (see
        // EdgeTreatment's own doc comment). Without either, a soft-alpha edge pixel keeps its full original
        // color (a blend of logo + fabric), which leaks the old fabric color through as a halo once composited
        // onto anything else.
        using SKBitmap? decontaminated = edgeTreatment == EdgeTreatment.Decontaminate ? ColorDecontamination.Decontaminate(image, mask) : null;
        using SKBitmap? choked = edgeTreatment == EdgeTreatment.Choke ? AlphaChoke.Apply(mask) : null;
        SKBitmap colorSource = decontaminated ?? image;
        SKBitmap alphaSource = choked ?? mask;

        // Composite via raw pixel spans, not GetPixel/SetPixel per pixel: a real phone photo is routinely 12+
        // MP, and GetPixel/SetPixel's per-call overhead made this loop visibly slow (multi-second) at that size.
        using var cutout = new SKBitmap(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKPixmap imagePx = colorSource.PeekPixels())
        using (SKPixmap maskPx = alphaSource.PeekPixels())
        using (SKPixmap cutoutPx = cutout.PeekPixels())
        {
            ReadOnlySpan<byte> imageBytes = imagePx.GetPixelSpan();
            ReadOnlySpan<byte> maskBytes = maskPx.GetPixelSpan();
            Span<byte> cutoutBytes = cutoutPx.GetPixelSpan();
            for (int i = 0; i + 3 < cutoutBytes.Length; i += 4)
            {
                cutoutBytes[i] = imageBytes[i];
                cutoutBytes[i + 1] = imageBytes[i + 1];
                cutoutBytes[i + 2] = imageBytes[i + 2];
                cutoutBytes[i + 3] = maskBytes[i]; // mask value lives in its R channel (see SegmentViewModel's own remarks on the format)
            }
        }
        using (SKData cutoutData = cutout.Encode(SKEncodedImageFormat.Png, 100))
        using (FileStream cutoutStream = File.Create(Path.Combine(folder, CutoutFileName)))
            cutoutData.SaveTo(cutoutStream);

        using SKBitmap thumb = cutout.Resize(new SKImageInfo(320, (int)(320.0 * cutout.Height / cutout.Width)), SKSamplingOptions.Default)!;
        using (SKData thumbData = thumb.Encode(SKEncodedImageFormat.Png, 100))
        using (FileStream thumbStream = File.Create(Path.Combine(folder, ThumbFileName)))
            thumbData.SaveTo(thumbStream);
    }

    /// <summary>A mask that keeps the whole image (R=255/alpha=255 everywhere) -- the dashboard's "Đánh dấu
    /// hoàn thành" override uses this instead of running the model, for exactly the case the owner reported
    /// 2026-10-28: auto-segmentation stuck/hung with no way to get the item unstuck.</summary>
    public static SKBitmap CreateFullKeepMask(int width, int height)
    {
        var mask = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKCanvas canvas = new(mask);
        canvas.Clear(new SKColor(255, 255, 255, 255));
        return mask;
    }
}
