using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Alternative to <see cref="ColorDecontamination"/> for the same "viền trắng" (white fringe) problem
/// -- owner's request, 2026-10-29: "có bao nhiêu phương án giải quyết... nếu có 2 cách thì làm thêm setting".
/// Decontamination re-estimates the TRUE color of each semi-transparent edge pixel from nearby high-alpha
/// neighbors, which can still leave a faint residual fringe if the soft-alpha transition band is wide enough
/// that the middle of it has no trustworthy neighbor close by. This takes a different, simpler approach: narrow
/// the alpha mask's own soft-edge band so far fewer pixels are left semi-transparent at all -- anything below
/// <paramref name="lowThreshold"/> goes fully transparent, anything above <paramref name="highThreshold"/> goes
/// fully opaque, only the (now much narrower) band between the two keeps a graded alpha. Less area where the
/// original (possibly fabric-contaminated) color can show through, regardless of how well it could be
/// re-estimated -- at the cost of a very slightly tighter cutout edge (a pixel or two of soft true boundary can
/// be lost to full transparency instead of a gradual fade). A pure per-pixel alpha remap, not a spatial/
/// neighborhood operation like ColorDecontamination's own box-sum -- cheaper, and does not need
/// <paramref name="image"/> at all, only the mask.</summary>
public static class AlphaChoke
{
    public static SKBitmap Apply(SKBitmap mask, byte lowThreshold = 50, byte highThreshold = 210)
    {
        int width = mask.Width, height = mask.Height;
        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKPixmap srcPx = mask.PeekPixels();
        using SKPixmap dstPx = result.PeekPixels();
        ReadOnlySpan<byte> src = srcPx.GetPixelSpan();
        Span<byte> dst = dstPx.GetPixelSpan();
        float range = Math.Max(highThreshold - lowThreshold, 1);
        for (int i = 0; i + 3 < src.Length; i += 4)
        {
            byte v = src[i]; // R channel carries the mask value, same convention as every mask this app produces
            byte choked = v <= lowThreshold ? (byte)0
                : v >= highThreshold ? (byte)255
                : (byte)Math.Clamp((v - lowThreshold) / range * 255f, 0, 255);
            dst[i] = choked; dst[i + 1] = choked; dst[i + 2] = choked; dst[i + 3] = 255;
        }
        return result;
    }
}
