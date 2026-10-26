namespace LogoExtract.Core;

/// <summary>Where one item is in the pipeline (PLAN.md section 1): segmentation is the only automatic/guaranteed
/// step; Flattened/ColorAdjusted/Vectorized are each a manual action the user explicitly takes (there is no
/// automatic wrinkle-flattening -- see PLAN.md section 3 "KHÔNG THỂ làm").</summary>
public enum ItemState
{
    /// <summary>Just imported, background removal not run yet (or still running in the queue).</summary>
    Imported,
    /// <summary>Background/logo segmentation finished (automatic model result, possibly touched up by hand).</summary>
    Segmented,
    /// <summary>User has gone through the manual flatten tool (grid warp / liquify) at least once.</summary>
    Flattened,
    /// <summary>User has applied a vectorize pass; a vector file exists alongside the raster.</summary>
    Vectorized,
}

/// <summary>One photo -> one extracted logo. All paths are relative to the item's own folder
/// (see <see cref="ItemStore.ItemFolder"/>), never absolute, so the whole library is portable across devices.</summary>
public sealed record LogoItem
{
    public required string Id { get; init; }
    public required string BatchId { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required ItemState State { get; init; }

    /// <summary>Original imported photo, untouched (file name inside the item folder).</summary>
    public required string OriginalFile { get; init; }
    /// <summary>Thumbnail for the items-view grid (file name inside the item folder, null until generated).</summary>
    public string? ThumbFile { get; init; }
    /// <summary>RGBA PNG result of segmentation (background removed), null before that step runs.</summary>
    public string? CutoutFile { get; init; }
    /// <summary>RGBA PNG result of the manual flatten tool, null until the user applies it at least once.</summary>
    public string? FlattenedFile { get; init; }
    /// <summary>SVG result of vectorize, null until the user runs that step.</summary>
    public string? VectorFile { get; init; }

    /// <summary>Mesh-warp control-grid state, so re-opening the flatten tool restores the user's last edit
    /// instead of starting from a fresh identity grid every time.</summary>
    public WarpGrid? Warp { get; init; }

    /// <summary>Manual color adjustment applied (0 = no change for every field).</summary>
    public ColorAdjust ColorAdjust { get; init; } = ColorAdjust.None;
}

/// <summary>One import/capture session's worth of items, so a batch of 10 photos shows as one group in the
/// items view instead of 10 unrelated rows (PLAN.md 2.6 "Thư mục/nhóm").</summary>
public sealed record BatchGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedUtc { get; init; }
}

/// <summary>An N x M grid of control points over the logo image; dragging a point locally warps the region
/// around it (bilinear interpolation between neighbouring points). Points are normalized 0..1 so the grid is
/// resolution-independent. A freshly segmented item gets an identity grid (every point at its rest position,
/// Dx=Dy=0) -- "no flattening applied yet", not "no grid exists".</summary>
public sealed record WarpGrid
{
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    /// <summary>Length Columns*Rows, row-major. Each entry is the point's displacement from its identity grid
    /// position, in the same normalized 0..1 space (so e.g. 0.02 = 2% of the image's width/height).</summary>
    public required IReadOnlyList<(double Dx, double Dy)> Displacements { get; init; }

    public static WarpGrid Identity(int columns = 5, int rows = 5)
    {
        var d = new (double, double)[columns * rows];
        Array.Fill(d, (0.0, 0.0));
        return new WarpGrid { Columns = columns, Rows = rows, Displacements = d };
    }
}

/// <summary>Manual color correction (PLAN.md 2.4): plain brightness/contrast/white-balance, no auto color
/// calibration (no reference-card detection -- that was cut along with the other server-dependent ideas).</summary>
public sealed record ColorAdjust
{
    public double Brightness { get; init; } // -1..1
    public double Contrast { get; init; }   // -1..1
    /// <summary>Per-channel multiplier from the "pick a neutral point" tool; (1,1,1) = no correction.</summary>
    public (double R, double G, double B) WhiteBalance { get; init; } = (1, 1, 1);

    public static readonly ColorAdjust None = new();
}
