using LogoExtract.Core.Warp;
using SkiaSharp;

namespace LogoExtract.Core.Tests;

public class MeshWarpTests
{
    private const int Size = 11; // W-1 = 10, so a 0.2 normalized displacement is an exact 2px shift.

    private static SKBitmap MakeColumnGradient()
    {
        var bmp = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x * 20), (byte)(y * 20), 0, 255));
        return bmp;
    }

    [Fact]
    public void Apply_IdentityGrid_ReturnsSourceUnchanged()
    {
        using SKBitmap source = MakeColumnGradient();
        WarpGrid grid = WarpGrid.Identity();

        using SKBitmap result = MeshWarp.Apply(source, grid);

        Assert.Equal(source.Width, result.Width);
        Assert.Equal(source.Height, result.Height);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                Assert.Equal(source.GetPixel(x, y), result.GetPixel(x, y));
    }

    [Fact]
    public void Apply_UniformDisplacement_ShiftsContentByExpectedPixelCount()
    {
        using SKBitmap source = MakeColumnGradient();
        // 2x2 grid, every corner displaced the same (0.2, 0) => a uniform 2px shift along X, none along Y.
        var grid = new WarpGrid
        {
            Columns = 2,
            Rows = 2,
            Displacements = [(0.2, 0.0), (0.2, 0.0), (0.2, 0.0), (0.2, 0.0)],
        };

        using SKBitmap result = MeshWarp.Apply(source, grid);

        for (int y = 0; y < Size; y++)
        {
            // Output column x samples source column (x - 2); columns before that have no source pixel to sample
            // from, so MeshWarp leaves them fully transparent (RGB is unspecified once alpha is 0, so only alpha
            // itself is asserted here).
            for (int x = 0; x < 2; x++)
                Assert.Equal((byte)0, result.GetPixel(x, y).Alpha);
            for (int x = 2; x < Size; x++)
                Assert.Equal(source.GetPixel(x - 2, y), result.GetPixel(x, y));
        }
    }

    [Fact]
    public void Apply_OutputIsRgba8888Unpremul()
    {
        using SKBitmap source = MakeColumnGradient();
        using SKBitmap result = MeshWarp.Apply(source, WarpGrid.Identity());

        Assert.Equal(SKColorType.Rgba8888, result.ColorType);
        Assert.Equal(SKAlphaType.Unpremul, result.AlphaType);
    }
}
