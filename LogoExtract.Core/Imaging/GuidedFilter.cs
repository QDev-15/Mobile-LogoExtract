using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Edge-aware alpha-mask refinement (He, Sun &amp; Tang, "Guided Image Filtering", 2010) -- classic
/// image-processing math, no AI model. U2NetSegmentationService's mask is upscaled from a 320x320 model output,
/// so its edges are inherently blurry and miss fine detail (hair strands, frayed fabric edges) no matter how
/// sharp the original photo is. This nudges the mask to follow the ORIGINAL full-resolution photo's real color
/// edges instead (owner's report, 2026-10-27: "tách vùng tóc chưa được rõ nét, không tách được từng sợi tóc
/// như ảnh test") -- the same idea as Photoshop's "Refine Edge"/"Select and Mask". It cannot invent detail the
/// 320px pass never saw in the first place, but a hair boundary blurred across dozens of pixels becomes one
/// that tracks the actual light/dark transition in the photo, which reads as "more detailed" even though no
/// individual strand is traced.</summary>
public static class GuidedFilter
{
    /// <summary>Guide and mask are resampled to at most <paramref name="maxDimension"/> on the long side before
    /// filtering -- the algorithm's per-pixel cost is independent of radius (summed-area tables) but still
    /// scales with pixel count, and a real phone photo (12+ MP) at full resolution would be tens of millions of
    /// doubles across 6 accumulator arrays, too slow and too much memory for a mid-range phone. The refined
    /// alpha is upscaled back to the mask's original size on return, so callers never see the downscale.</summary>
    public static SKBitmap Refine(SKBitmap guide, SKBitmap alphaMask, int maxDimension = 1400, int radius = 8, float epsilon = 1e-3f)
    {
        int origWidth = alphaMask.Width, origHeight = alphaMask.Height;
        float scale = Math.Min(1f, maxDimension / (float)Math.Max(origWidth, origHeight));
        int width = Math.Max(1, (int)(origWidth * scale));
        int height = Math.Max(1, (int)(origHeight * scale));

        using SKBitmap guideSmall = ResizeTo(guide, width, height);
        using SKBitmap maskSmall = ResizeTo(alphaMask, width, height);

        float[] guideGray = ToGrayFloat(guideSmall);
        float[] p = ToAlphaFloat(maskSmall);
        int n = width * height;

        var Ip = new float[n];
        var II = new float[n];
        for (int i = 0; i < n; i++)
        {
            Ip[i] = guideGray[i] * p[i];
            II[i] = guideGray[i] * guideGray[i];
        }

        double[] sumI = BuildSat(guideGray, width, height);
        double[] sumP = BuildSat(p, width, height);
        double[] sumIp = BuildSat(Ip, width, height);
        double[] sumII = BuildSat(II, width, height);

        var a = new float[n];
        var b = new float[n];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                int count = WindowCount(x, y, width, height, radius);
                float meanI = (float)(BoxSum(sumI, width, height, x, y, radius) / count);
                float meanP = (float)(BoxSum(sumP, width, height, x, y, radius) / count);
                float meanIp = (float)(BoxSum(sumIp, width, height, x, y, radius) / count);
                float meanII = (float)(BoxSum(sumII, width, height, x, y, radius) / count);
                float covIp = meanIp - meanI * meanP;
                float varI = meanII - meanI * meanI;
                float ai = covIp / (varI + epsilon);
                a[idx] = ai;
                b[idx] = meanP - ai * meanI;
            }
        }

        double[] sumA = BuildSat(a, width, height);
        double[] sumB = BuildSat(b, width, height);

        using var refinedSmall = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKPixmap px = refinedSmall.PeekPixels())
        {
            Span<byte> dst = px.GetPixelSpan();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    int count = WindowCount(x, y, width, height, radius);
                    float meanA = (float)(BoxSum(sumA, width, height, x, y, radius) / count);
                    float meanB = (float)(BoxSum(sumB, width, height, x, y, radius) / count);
                    float q = meanA * guideGray[idx] + meanB;
                    byte v = (byte)Math.Clamp(q * 255f, 0f, 255f);
                    int o = idx * 4;
                    dst[o] = v; dst[o + 1] = v; dst[o + 2] = v; dst[o + 3] = 255;
                }
            }
        }

        return scale >= 1f ? refinedSmall.Copy() : ResizeTo(refinedSmall, origWidth, origHeight);
    }

    private static SKBitmap ResizeTo(SKBitmap src, int width, int height)
    {
        if (src.Width == width && src.Height == height) return src.Copy();
        return src.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
    }

    private static float[] ToGrayFloat(SKBitmap src)
    {
        using SKPixmap px = src.PeekPixels();
        ReadOnlySpan<byte> span = px.GetPixelSpan();
        var result = new float[src.Width * src.Height];
        for (int i = 0; i < result.Length; i++)
        {
            int o = i * 4;
            // Rec. 601 luma weights -- this is for edge structure, not color-accurate output.
            result[i] = (0.299f * span[o] + 0.587f * span[o + 1] + 0.114f * span[o + 2]) / 255f;
        }
        return result;
    }

    private static float[] ToAlphaFloat(SKBitmap mask)
    {
        using SKPixmap px = mask.PeekPixels();
        ReadOnlySpan<byte> span = px.GetPixelSpan();
        var result = new float[mask.Width * mask.Height];
        for (int i = 0; i < result.Length; i++) result[i] = span[i * 4] / 255f; // R channel carries the mask value
        return result;
    }

    /// <summary>Summed-area table (1 extra row/column of padding) so any window's sum is 4 lookups regardless
    /// of radius -- a naive box filter would be O(radius^2) per pixel, slow enough on a 1400px image to matter.</summary>
    private static double[] BuildSat(float[] src, int w, int h)
    {
        var sat = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            int satRow = (y + 1) * (w + 1);
            int satPrevRow = y * (w + 1);
            for (int x = 0; x < w; x++)
            {
                rowSum += src[y * w + x];
                sat[satRow + x + 1] = sat[satPrevRow + x + 1] + rowSum;
            }
        }
        return sat;
    }

    private static double BoxSum(double[] sat, int w, int h, int cx, int cy, int radius)
    {
        int x0 = Math.Max(0, cx - radius), y0 = Math.Max(0, cy - radius);
        int x1 = Math.Min(w - 1, cx + radius), y1 = Math.Min(h - 1, cy + radius);
        int sw = w + 1;
        return sat[(y1 + 1) * sw + (x1 + 1)] - sat[y0 * sw + (x1 + 1)] - sat[(y1 + 1) * sw + x0] + sat[y0 * sw + x0];
    }

    private static int WindowCount(int cx, int cy, int w, int h, int radius)
    {
        int x0 = Math.Max(0, cx - radius), y0 = Math.Max(0, cy - radius);
        int x1 = Math.Min(w - 1, cx + radius), y1 = Math.Min(h - 1, cy + radius);
        return (x1 - x0 + 1) * (y1 - y0 + 1);
    }
}
