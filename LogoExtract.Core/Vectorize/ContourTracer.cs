using SkiaSharp;

namespace LogoExtract.Core.Vectorize;

/// <summary>One closed polygon boundary, already simplified.</summary>
public sealed record Contour(IReadOnlyList<(double X, double Y)> Points);

/// <summary>v1 vectorize (PLAN.md 2.5): traces the OUTER SILHOUETTE of the alpha mask into simplified polygon
/// paths and emits them as an SVG. This is a cut/contour path (useful as-is for vinyl cutters / heat-transfer
/// cutting machines), NOT a full multi-color region vectorization of the logo's internal artwork -- that is a
/// materially bigger algorithm (color quantization + per-region tracing) cut from tonight's scope. Says so in
/// the exported SVG's own title/description so nobody mistakes one for the other.</summary>
public static class ContourTracer
{
    /// <summary>Alpha values &gt;= this are "inside" for tracing purposes.</summary>
    public const byte AlphaThreshold = 32;

    /// <summary><paramref name="alphaMask"/> must be Rgba8888 (see Core.Imaging.BitmapIO) -- read via a raw
    /// pixel span below, not GetPixel per pixel: a review-2026-10-27 perf pass found GetPixel's interop overhead
    /// made this O(width*height) pass noticeably slow on a real multi-MP photo.</summary>
    public static IReadOnlyList<Contour> Trace(SKBitmap alphaMask, double simplifyEpsilonPx = 1.5)
    {
        int w = alphaMask.Width, h = alphaMask.Height;
        bool[,] inside = new bool[w, h];
        using (SKPixmap pixmap = alphaMask.PeekPixels())
        {
            ReadOnlySpan<byte> bytes = pixmap.GetPixelSpan();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    inside[x, y] = bytes[(y * w + x) * 4] >= AlphaThreshold; // mask value lives in the R channel
        }

        bool[,] visited = new bool[alphaMask.Width, alphaMask.Height];
        List<Contour> contours = [];
        for (int y = 0; y < alphaMask.Height; y++)
        {
            for (int x = 0; x < alphaMask.Width; x++)
            {
                if (!inside[x, y] || visited[x, y]) continue;
                // A boundary pixel: inside, with at least one outside/edge neighbor (4-connected).
                bool isBoundary = x == 0 || y == 0 || x == alphaMask.Width - 1 || y == alphaMask.Height - 1
                    || !inside[x - 1, y] || !inside[x + 1, y] || !inside[x, y - 1] || !inside[x, y + 1];
                if (!isBoundary) continue;

                List<(int X, int Y)> raw = MooreTrace(inside, visited, x, y);
                if (raw.Count < 8) continue; // too small to be a real contour (noise)
                IReadOnlyList<(double, double)> simplified = DouglasPeucker(
                    [.. raw.Select(p => ((double)p.X, (double)p.Y))], simplifyEpsilonPx);
                contours.Add(new Contour(simplified));
            }
        }
        return contours;
    }

    /// <summary>Moore-neighbor boundary tracing: walk the outer edge of one connected blob starting at
    /// (startX,startY), marking every pixel visited along the way so the outer loop never re-traces the same
    /// blob as a second "new" contour.</summary>
    private static List<(int X, int Y)> MooreTrace(bool[,] inside, bool[,] visited, int startX, int startY)
    {
        int w = inside.GetLength(0), h = inside.GetLength(1);
        (int dx, int dy)[] dirs =
        [
            (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
        ];
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && inside[x, y];

        List<(int, int)> boundary = [];
        (int x, int y) current = (startX, startY);
        int backtrackDir = 6; // arbitrary start search direction (pointing "up")
        (int x, int y) first = current;
        int safety = w * h * 2 + 8; // hard cap: a pathological mask must never spin this loop forever

        do
        {
            boundary.Add(current);
            visited[current.x, current.y] = true;
            bool found = false;
            for (int i = 0; i < 8; i++)
            {
                int dir = (backtrackDir + i) % 8;
                (int nx, int ny) = (current.x + dirs[dir].dx, current.y + dirs[dir].dy);
                if (In(nx, ny))
                {
                    current = (nx, ny);
                    backtrackDir = (dir + 5) % 8; // next search starts just behind where we came from
                    found = true;
                    break;
                }
            }
            if (!found) break; // isolated single pixel
        } while (current != first && --safety > 0);

        return boundary;
    }

    /// <summary>Standard recursive Douglas-Peucker polyline simplification.</summary>
    private static IReadOnlyList<(double X, double Y)> DouglasPeucker(IReadOnlyList<(double X, double Y)> points, double epsilon)
    {
        if (points.Count < 3) return points;
        double maxDist = 0;
        int index = 0;
        (double X, double Y) a = points[0], b = points[^1];
        for (int i = 1; i < points.Count - 1; i++)
        {
            double d = PerpendicularDistance(points[i], a, b);
            if (d > maxDist) { maxDist = d; index = i; }
        }
        if (maxDist <= epsilon) return [a, b];

        IReadOnlyList<(double, double)> left = DouglasPeucker(points.Take(index + 1).ToList(), epsilon);
        IReadOnlyList<(double, double)> right = DouglasPeucker(points.Skip(index).ToList(), epsilon);
        return [.. left.Take(left.Count - 1), .. right];
    }

    private static double PerpendicularDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (len * len);
        double projX = a.X + t * dx, projY = a.Y + t * dy;
        return Math.Sqrt(Math.Pow(p.X - projX, 2) + Math.Pow(p.Y - projY, 2));
    }

    public static string ToSvg(IReadOnlyList<Contour> contours, int width, int height, string fillHex = "#000000")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}">""");
        sb.AppendLine("  <!-- LogoExtract v1 vectorize: outer-silhouette cut path (single color), not a multi-color region trace. -->");
        foreach (Contour c in contours)
        {
            if (c.Points.Count < 3) continue;
            string d = "M " + string.Join(" L ", c.Points.Select(p => $"{p.X:0.##},{p.Y:0.##}")) + " Z";
            sb.AppendLine($"""  <path d="{d}" fill="{fillHex}" fill-rule="evenodd" />""");
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
