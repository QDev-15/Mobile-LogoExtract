using SkiaSharp;

namespace LogoExtract.Core.Warp;

/// <summary>Applies a <see cref="WarpGrid"/> to an image: inverse-maps each OUTPUT pixel back through the grid
/// to find which INPUT pixel to sample (standard inverse-warp, avoids holes that a forward-warp would leave).
/// Each grid cell is warped independently via bilinear interpolation of its 4 corners' displacements -- simple,
/// fast enough for interactive dragging on a phone, no need for a true thin-plate-spline for v1 (PLAN.md 2.3).</summary>
public static class MeshWarp
{
    public static SKBitmap Apply(SKBitmap source, WarpGrid grid)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        int cols = grid.Columns, rows = grid.Rows;

        for (int oy = 0; oy < source.Height; oy++)
        {
            double v = (double)oy / Math.Max(source.Height - 1, 1) * (rows - 1);
            int row = Math.Clamp((int)v, 0, rows - 2 < 0 ? 0 : rows - 2);
            double vFrac = rows > 1 ? v - row : 0;

            for (int ox = 0; ox < source.Width; ox++)
            {
                double u = (double)ox / Math.Max(source.Width - 1, 1) * (cols - 1);
                int col = Math.Clamp((int)u, 0, cols - 2 < 0 ? 0 : cols - 2);
                double uFrac = cols > 1 ? u - col : 0;

                (double dx, double dy) = BilinearDisplacement(grid, col, row, uFrac, vFrac);

                // The displacement is defined in the FORWARD direction (how the identity grid point moved to
                // flatten the wrinkle); sampling the source for a given output pixel needs the inverse, so
                // subtract rather than add.
                double srcXNorm = (double)ox / Math.Max(source.Width - 1, 1) - dx;
                double srcYNorm = (double)oy / Math.Max(source.Height - 1, 1) - dy;
                int srcX = (int)Math.Round(srcXNorm * (source.Width - 1));
                int srcY = (int)Math.Round(srcYNorm * (source.Height - 1));

                result.SetPixel(ox, oy,
                    srcX >= 0 && srcY >= 0 && srcX < source.Width && srcY < source.Height
                        ? source.GetPixel(srcX, srcY)
                        : SKColors.Transparent);
            }
        }
        return result;
    }

    private static (double Dx, double Dy) BilinearDisplacement(WarpGrid grid, int col, int row, double uFrac, double vFrac)
    {
        (double Dx, double Dy) At(int c, int r) => grid.Displacements[r * grid.Columns + c];
        (double Dx, double Dy) p00 = At(col, row);
        (double Dx, double Dy) p10 = At(Math.Min(col + 1, grid.Columns - 1), row);
        (double Dx, double Dy) p01 = At(col, Math.Min(row + 1, grid.Rows - 1));
        (double Dx, double Dy) p11 = At(Math.Min(col + 1, grid.Columns - 1), Math.Min(row + 1, grid.Rows - 1));

        double dx = Lerp(Lerp(p00.Dx, p10.Dx, uFrac), Lerp(p01.Dx, p11.Dx, uFrac), vFrac);
        double dy = Lerp(Lerp(p00.Dy, p10.Dy, uFrac), Lerp(p01.Dy, p11.Dy, uFrac), vFrac);
        return (dx, dy);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
