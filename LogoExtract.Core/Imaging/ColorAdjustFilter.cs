using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Applies a <see cref="ColorAdjust"/> (PLAN.md 2.4: manual brightness/contrast/white-balance, no
/// automatic color calibration). Alpha is left untouched -- only RGB is adjusted.
///
/// <para><b>Perf note (2026-10-27 review)</b>: the first version ran the brightness/contrast/white-balance
/// formula as floating-point math inside a per-pixel GetPixel/SetPixel loop -- on a real phone photo (12+ MP)
/// that is millions of redundant identical computations (the formula only depends on the input BYTE VALUE 0..255
/// per channel, not on position) plus GetPixel/SetPixel's own interop overhead. Rewritten to precompute a 256-
/// entry lookup table per channel ONCE, then just index into it via a raw pixel span -- same technique
/// DocScanner's own ToneAdjust already uses for this exact class of operation ("LUT khi lưu").</para></summary>
public static class ColorAdjustFilter
{
    /// <summary>Requires <paramref name="source"/> to be Rgba8888 (see Core.Imaging.BitmapIO) -- read and
    /// written via raw pixel spans, not GetPixel/SetPixel.</summary>
    public static SKBitmap Apply(SKBitmap source, ColorAdjust adjust)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        double brightness = adjust.Brightness * 255;
        double contrast = Math.Tan((adjust.Contrast + 1) * Math.PI / 4); // -1..1 -> 0..inf, 0 = no change

        byte[] lutR = BuildLut(adjust.WhiteBalance.R, brightness, contrast);
        byte[] lutG = BuildLut(adjust.WhiteBalance.G, brightness, contrast);
        byte[] lutB = BuildLut(adjust.WhiteBalance.B, brightness, contrast);

        using SKPixmap srcPx = source.PeekPixels();
        using SKPixmap dstPx = result.PeekPixels();
        ReadOnlySpan<byte> src = srcPx.GetPixelSpan();
        Span<byte> dst = dstPx.GetPixelSpan();
        for (int i = 0; i + 3 < src.Length; i += 4)
        {
            dst[i] = lutR[src[i]];
            dst[i + 1] = lutG[src[i + 1]];
            dst[i + 2] = lutB[src[i + 2]];
            dst[i + 3] = src[i + 3]; // alpha untouched
        }

        // Saturation is NOT independent per channel (it mixes R/G/B via a luminance weighting), unlike
        // Brightness/Contrast/WhiteBalance above, so it cannot be folded into the same per-channel LUT -- applied
        // here as a second Span-based pass over the already brightness/contrast-adjusted `result` (merged edit
        // screen, 2026-10-29). Skipped entirely when 0 (the common case -- most items never touch this slider).
        if (adjust.Saturation != 0) ApplySaturation(dstPx, adjust.Saturation);
        return result;
    }

    /// <summary>Mixes each pixel toward (Saturation&lt;0) or away from (Saturation&gt;0) its own luma, using the
    /// same 0.3086/0.6094/0.0820 R/G/B weights LogoExtract.Views.AdjustablePreviewView's live-preview saturation
    /// color matrix uses, so the full-resolution export and the on-screen preview agree. Span-based (no
    /// GetPixel/SetPixel), consistent with the rest of this file.</summary>
    private static void ApplySaturation(SKPixmap pixmap, double saturation)
    {
        double satFactor = 1 + saturation; // -1 = fully gray, 0 = unchanged, +1 = oversaturated
        Span<byte> span = pixmap.GetPixelSpan();
        for (int i = 0; i + 3 < span.Length; i += 4)
        {
            byte r = span[i], g = span[i + 1], b = span[i + 2];
            double gray = 0.3086 * r + 0.6094 * g + 0.0820 * b;
            span[i] = Clamp(gray + (r - gray) * satFactor);
            span[i + 1] = Clamp(gray + (g - gray) * satFactor);
            span[i + 2] = Clamp(gray + (b - gray) * satFactor);
            // span[i + 3] (alpha) untouched
        }
    }

    private static byte[] BuildLut(double whiteBalanceMultiplier, double brightness, double contrast)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double x = v * whiteBalanceMultiplier;
            x = (x - 128) * contrast + 128 + brightness;
            lut[v] = Clamp(x);
        }
        return lut;
    }

    /// <summary>"Pick a neutral point" tool (PLAN.md 2.4): the user taps a pixel they know should be neutral
    /// gray/white; this returns the per-channel multiplier that would make it exactly neutral, to feed into
    /// <see cref="ColorAdjust.WhiteBalance"/>.</summary>
    public static (double R, double G, double B) ComputeWhiteBalance(SKColor sampledNeutralPoint)
    {
        double target = (sampledNeutralPoint.Red + sampledNeutralPoint.Green + sampledNeutralPoint.Blue) / 3.0;
        if (target < 1) target = 1;
        return (
            target / Math.Max((int)sampledNeutralPoint.Red, 1),
            target / Math.Max((int)sampledNeutralPoint.Green, 1),
            target / Math.Max((int)sampledNeutralPoint.Blue, 1));
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
}
