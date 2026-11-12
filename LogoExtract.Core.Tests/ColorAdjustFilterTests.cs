using LogoExtract.Core.Imaging;
using SkiaSharp;

namespace LogoExtract.Core.Tests;

public class ColorAdjustFilterTests
{
    private static SKBitmap MakeBitmap(params SKColor[] pixels)
    {
        var bmp = new SKBitmap(pixels.Length, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int x = 0; x < pixels.Length; x++) bmp.SetPixel(x, 0, pixels[x]);
        return bmp;
    }

    [Fact]
    public void Apply_NoAdjust_LeavesPixelsUnchanged()
    {
        using SKBitmap source = MakeBitmap(new SKColor(0, 0, 0, 255), new SKColor(128, 64, 32, 200), new SKColor(255, 255, 255, 0));
        using SKBitmap result = ColorAdjustFilter.Apply(source, ColorAdjust.None);

        for (int x = 0; x < source.Width; x++)
            Assert.Equal(source.GetPixel(x, 0), result.GetPixel(x, 0));
    }

    [Fact]
    public void Apply_AlphaIsNeverModified()
    {
        using SKBitmap source = MakeBitmap(new SKColor(10, 20, 30, 77));
        var adjust = new ColorAdjust { Brightness = 1, Contrast = 1, Saturation = 1, WhiteBalance = (2, 2, 2) };

        using SKBitmap result = ColorAdjustFilter.Apply(source, adjust);

        Assert.Equal((byte)77, result.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Apply_MinimumContrast_CrushesEveryPixelToTheSameMidGray()
    {
        // Contrast = -1 => Math.Tan(0) = 0 exactly, so (v - 128) * 0 vanishes regardless of the input value:
        // every pixel must come out at exactly 128 + brightness, independent of its original color.
        using SKBitmap source = MakeBitmap(new SKColor(0, 10, 250, 255), new SKColor(255, 0, 128, 255));
        var adjust = new ColorAdjust { Brightness = -0.5, Contrast = -1 };

        using SKBitmap result = ColorAdjustFilter.Apply(source, adjust);

        for (int x = 0; x < source.Width; x++)
        {
            SKColor c = result.GetPixel(x, 0);
            Assert.Equal((byte)0, c.Red);
            Assert.Equal((byte)0, c.Green);
            Assert.Equal((byte)0, c.Blue);
        }
    }

    [Fact]
    public void Apply_Brightness_IncreasesEveryChannelByTheExpectedAmount()
    {
        // Same Contrast=-1 trick isolates brightness cleanly: output is exactly 128 + brightness*255 everywhere.
        using SKBitmap source = MakeBitmap(new SKColor(0, 0, 0, 255), new SKColor(255, 255, 255, 255));
        var adjust = new ColorAdjust { Brightness = 0.5, Contrast = -1 };

        using SKBitmap result = ColorAdjustFilter.Apply(source, adjust);

        byte expected = (byte)(128 + 0.5 * 255); // 255 (clamped)
        Assert.Equal(expected, result.GetPixel(0, 0).Red);
        Assert.Equal(expected, result.GetPixel(1, 0).Red);
    }

    [Fact]
    public void Apply_Saturation_MinusOne_MakesEveryChannelEqualToLuma()
    {
        using SKBitmap source = MakeBitmap(new SKColor(255, 0, 0, 255));
        var adjust = new ColorAdjust { Saturation = -1 };

        using SKBitmap result = ColorAdjustFilter.Apply(source, adjust);
        SKColor c = result.GetPixel(0, 0);

        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
        // Luma of pure red (0.3086*255) ~ 78.7; brightness/contrast defaults are no-ops here.
        Assert.InRange(c.Red, 77, 80);
    }

    [Fact]
    public void ComputeWhiteBalance_NeutralGray_ReturnsNoCorrection()
    {
        (double R, double G, double B) wb = ColorAdjustFilter.ComputeWhiteBalance(new SKColor(120, 120, 120));
        Assert.Equal(1.0, wb.R, precision: 10);
        Assert.Equal(1.0, wb.G, precision: 10);
        Assert.Equal(1.0, wb.B, precision: 10);
    }

    [Fact]
    public void ComputeWhiteBalance_TintedPoint_ReturnsPerChannelMultiplierTowardNeutral()
    {
        // Sampled point is too green/too weak on red and blue; the multiplier must boost R/B and reduce G
        // so that applying it would land all three channels on the same target (their average).
        var sample = new SKColor(90, 150, 60);
        (double R, double G, double B) wb = ColorAdjustFilter.ComputeWhiteBalance(sample);
        double target = (90 + 150 + 60) / 3.0;

        Assert.Equal(target / 90.0, wb.R, precision: 10);
        Assert.Equal(target / 150.0, wb.G, precision: 10);
        Assert.Equal(target / 60.0, wb.B, precision: 10);
        Assert.True(wb.R > 1); // red was under-represented, needs boosting
        Assert.True(wb.G < 1); // green was over-represented, needs reducing
        Assert.True(wb.B > 1); // blue was under-represented, needs boosting
    }
}
