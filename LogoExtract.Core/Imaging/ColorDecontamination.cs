using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Removes background-color "fringing"/halo from a cutout's edge pixels (owner's report, 2026-10-27:
/// "nhìn nó có viền trắng nhìn ảo quá" -- became visible once GuidedFilter widened the soft-alpha transition at
/// edges, 2026-10-27 earlier the same day). A pixel at alpha=0.5 on a logo's edge is really a 50/50 blend of
/// the logo's true color and the surrounding fabric's color, but this app's compositor keeps that pixel's FULL
/// original RGB and only lowers its alpha -- so when it lands on any background OTHER than that original
/// fabric, the fabric's own color still shows through as a visible rim.
///
/// <para>Standard fix (same idea as Photoshop's "Decontaminate Colors" in Select and Mask): for each low-alpha
/// edge pixel, re-estimate its "true" foreground color from nearby HIGH-alpha (confidently-foreground) pixels
/// instead of trusting its own blended color, then blend that estimate in proportionally to how little alpha
/// the pixel has. Pixels deep inside solid foreground (alpha near 1) are left untouched -- no fringing there,
/// and blurring would needlessly soften real internal detail (e.g. dark hair inside a light-colored logo).</para></summary>
public static class ColorDecontamination
{
    public static SKBitmap Decontaminate(SKBitmap image, SKBitmap alphaMask, int radius = 6)
    {
        int width = image.Width, height = image.Height;
        int n = width * height;

        using SKPixmap imgPx = image.PeekPixels();
        ReadOnlySpan<byte> imgBytes = imgPx.GetPixelSpan();
        using SKPixmap maskPx = alphaMask.PeekPixels();
        ReadOnlySpan<byte> maskBytes = maskPx.GetPixelSpan();

        var alpha = new float[n];
        var rA = new float[n];
        var gA = new float[n];
        var bA = new float[n]; // premultiplied: color * alpha, so a box sum naturally weights high-alpha (trustworthy) pixels more
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            float a = maskBytes[o] / 255f; // R channel carries the mask value (see MaskEditorView's field comment)
            alpha[i] = a;
            rA[i] = imgBytes[o] / 255f * a;
            gA[i] = imgBytes[o + 1] / 255f * a;
            bA[i] = imgBytes[o + 2] / 255f * a;
        }

        double[] sumAlpha = BuildSat(alpha, width, height);
        double[] sumR = BuildSat(rA, width, height);
        double[] sumG = BuildSat(gA, width, height);
        double[] sumB = BuildSat(bA, width, height);

        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKPixmap outPx = result.PeekPixels())
        {
            Span<byte> dst = outPx.GetPixelSpan();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    int o = idx * 4;
                    float a = alpha[idx];
                    float origR = imgBytes[o] / 255f, origG = imgBytes[o + 1] / 255f, origB = imgBytes[o + 2] / 255f;

                    float finalR = origR, finalG = origG, finalB = origB;
                    if (a < 0.98f) // skip solid-foreground interior entirely -- see class remarks
                    {
                        double wa = BoxSum(sumAlpha, width, height, x, y, radius);
                        if (wa > 1e-3)
                        {
                            float estR = (float)(BoxSum(sumR, width, height, x, y, radius) / wa);
                            float estG = (float)(BoxSum(sumG, width, height, x, y, radius) / wa);
                            float estB = (float)(BoxSum(sumB, width, height, x, y, radius) / wa);
                            // alpha=1 -> keep the pixel's own color untouched; alpha=0 -> fully trust the
                            // neighborhood estimate (irrelevant for display since it's fully transparent, but
                            // keeps edges clean if the alpha is later nudged up by hand).
                            finalR = origR * a + estR * (1 - a);
                            finalG = origG * a + estG * (1 - a);
                            finalB = origB * a + estB * (1 - a);
                        }
                    }

                    dst[o] = (byte)Math.Clamp(finalR * 255f, 0, 255);
                    dst[o + 1] = (byte)Math.Clamp(finalG * 255f, 0, 255);
                    dst[o + 2] = (byte)Math.Clamp(finalB * 255f, 0, 255);
                    dst[o + 3] = 255;
                }
            }
        }
        return result;
    }

    /// <summary>Summed-area table -- same technique as GuidedFilter, for the same reason (any window's sum in
    /// 4 lookups regardless of radius).</summary>
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
}
