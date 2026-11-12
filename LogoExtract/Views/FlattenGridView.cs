using LogoExtract.Core;
using LogoExtract.Core.Warp;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace LogoExtract.Views;

/// <summary>EditPage's manual "flatten" sub-mode (PLAN.md 2.3: a draggable mesh-warp control-point grid, since
/// this app does 100% on-device segmentation with no AI unwarping -- CLAUDE.md's own framing for why this tool
/// exists at all: "tách là chính, méo để người dùng tự sửa"). Raw SkiaSharp canvas + hand-rolled touch handling,
/// same shape as MaskEditorView/AdjustablePreviewView in this same project (not GraphicsView/
/// PinchGestureRecognizer -- this app's own established reason not to, see AdjustablePreviewView's class doc
/// comment on the perf cost of MAUI's gesture pipeline on Android).
///
/// <para><b>Why this view never disposes (or keeps a long-lived reference to) the bitmap passed into
/// <see cref="Load"/></b>: EditViewModel owns the full-resolution "raw" cutout (its own <c>_flattenSource</c>
/// field, deliberately the UN-warped cutout.png even if a FlattenedFile already exists -- see
/// EditViewModel.OpenFlattenToolAsync's own remarks) and disposes it itself on Cancel/Apply/Unload. This view
/// only ever reads it once per <see cref="Load"/> call, to build its OWN downscaled copy
/// (<see cref="_previewSource"/>), which is the only bitmap re-warped on every drag -- warping at full 12+MP
/// resolution on every touch-move frame would be far too slow for interactive dragging (the same class of
/// problem MaskEditorView's own class doc comment describes for GetPixel/SetPixel at full photo resolution,
/// except here the cost is MeshWarp.Apply's own per-output-pixel GetPixel/SetPixel loop, which that class
/// deliberately does NOT rewrite to be Span-based -- see its own remarks on why bilinear-interpolated inverse
/// sampling doesn't map cleanly onto a flat byte-span pass the way a straight per-channel LUT does). The actual
/// full-resolution warp only ever runs once, in EditViewModel.ApplyFlattenAsync, when the user commits.</para></summary>
public sealed class FlattenGridView : SKCanvasView
{
    // Long side of the downscaled preview copy this view builds for itself -- small enough that re-running
    // MeshWarp.Apply's GetPixel/SetPixel loop on it stays reasonably responsive while dragging on a real phone.
    // Not validated against an actual device in this session (see this app's own CLAUDE.md note on why); if a
    // real device still feels laggy, lowering this constant is the first thing to try before anything else.
    private const int PreviewMaxDimension = 480;

    // mobile-app-design.md §7's 44dp minimum touch target, reused here as the drag-pickup radius so a control
    // point is at least as easy to grab as any other interactive element in this app.
    private const float HitRadiusPx = 44;

    // Caps how often the full preview re-warp runs while a finger is actively dragging a point -- the dragged
    // point itself still moves/redraws every touch-move event (see OnTouch), only the expensive re-warp is
    // throttled, so the grid always tracks the finger exactly even on a slow device; only the warped image
    // underneath it can lag a frame or two behind.
    private const long RecomputeThrottleMs = 60;

    private static readonly SKColor GridColor = new(31, 174, 130); // #1FAE82, this app's own brand mint -- a hardcoded literal, not AppThemeBinding'd, same as MaskEditorView's own tint: this canvas always paints on its own opaque black backdrop regardless of the app's light/dark theme, so there is no second variant to pick between (see mobile-app-design.md §8 gotcha 5 on why code-drawn colors need a reason to skip theme binding, not just an oversight -- this is that reason).
    private static readonly SKColor ActivePointColor = SKColors.White;

    private SKBitmap? _previewSource; // this view's OWN downscaled copy -- never the bitmap handed to Load()
    private SKBitmap? _warped; // latest MeshWarp.Apply(_previewSource, _grid) result, redrawn on each recompute
    private (double Dx, double Dy)[] _displacements = [];
    private WarpGrid _grid = WarpGrid.Identity();
    private int _draggingIndex = -1;
    private long _lastRecomputeMs;

    public FlattenGridView()
    {
        EnableTouchEvents = true;
        Touch += OnTouch;
        PaintSurface += OnPaintSurface;
    }

    /// <summary>The grid as currently edited, including any in-progress drag -- read by EditPage's code-behind
    /// when the user taps "Áp dụng". Same "caller reads the live field directly, no copy" ownership shape as
    /// MaskEditorView.CurrentMask.</summary>
    public WarpGrid CurrentGrid => _grid;

    /// <summary>Takes a READ-ONLY look at <paramref name="fullResSource"/> (see class remarks -- never disposes
    /// or keeps a reference to it beyond this call), builds this view's own downscaled preview copy, and starts
    /// from <paramref name="grid"/> as the restored control-point state.</summary>
    public void Load(SKBitmap fullResSource, WarpGrid grid)
    {
        _previewSource?.Dispose();
        _previewSource = DownscaleForPreview(fullResSource, PreviewMaxDimension);
        // Own copy of the array, not grid.Displacements itself -- this view mutates individual elements in
        // place while dragging (see UpdateDraggedPoint), and the WarpGrid handed in may be the SAME array
        // instance LogoItem.Warp was deserialized into; mutating that directly would be surprising action-at-a-
        // distance if anything else still held a reference to the original LogoItem record.
        _displacements = [.. grid.Displacements];
        _grid = new WarpGrid { Columns = grid.Columns, Rows = grid.Rows, Displacements = _displacements };
        _draggingIndex = -1;
        RecomputeWarp();
        InvalidateSurface();
    }

    /// <summary>Back to an all-zero grid, same column/row count -- the view's own "Đặt lại lưới" action. Local
    /// to this view only; EditViewModel never sees this until the user taps "Áp dụng" afterwards.</summary>
    public void ResetGrid()
    {
        Array.Fill(_displacements, (0.0, 0.0));
        RecomputeWarp();
        InvalidateSurface();
    }

    private static SKBitmap DownscaleForPreview(SKBitmap source, int maxDimension)
    {
        // Same shape as EditViewModel.DownscaleForPreview -- duplicated rather than shared, since that method is
        // private to a different class and this one additionally must never take ownership of its input (see
        // class remarks); not worth a shared public utility for 6 lines used by exactly two call sites.
        int longSide = Math.Max(source.Width, source.Height);
        if (longSide <= maxDimension) return source.Copy();
        float scale = maxDimension / (float)longSide;
        int width = Math.Max(1, (int)(source.Width * scale));
        int height = Math.Max(1, (int)(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
    }

    private void RecomputeWarp()
    {
        if (_previewSource == null) return;
        SKBitmap newWarped = MeshWarp.Apply(_previewSource, _grid);
        _warped?.Dispose();
        _warped = newWarped;
    }

    private (float Scale, float OffsetX, float OffsetY) FitTransform()
    {
        if (_previewSource == null || CanvasSize.Width <= 0 || CanvasSize.Height <= 0) return (1, 0, 0);
        float scale = Math.Min(CanvasSize.Width / _previewSource.Width, CanvasSize.Height / _previewSource.Height);
        float offX = (CanvasSize.Width - _previewSource.Width * scale) / 2;
        float offY = (CanvasSize.Height - _previewSource.Height * scale) / 2;
        return (scale, offX, offY);
    }

    /// <summary>Canvas-pixel position of grid point (col,row): its identity rest position plus its current
    /// displacement (both normalized 0..1, the same convention WarpGrid's own doc comment describes), mapped
    /// through <see cref="FitTransform"/>.</summary>
    private SKPoint PointCanvasPos(int col, int row, float scale, float offX, float offY)
    {
        if (_previewSource == null) return default;
        (double dx, double dy) = _displacements[row * _grid.Columns + col];
        double nx = (double)col / Math.Max(_grid.Columns - 1, 1) + dx;
        double ny = (double)row / Math.Max(_grid.Rows - 1, 1) + dy;
        return new SKPoint(offX + (float)(nx * _previewSource.Width * scale), offY + (float)(ny * _previewSource.Height * scale));
    }

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        if (_previewSource == null) { e.Handled = true; return; }
        (float scale, float offX, float offY) = FitTransform();

        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _draggingIndex = NearestPointIndex(e.Location, scale, offX, offY);
                InvalidateSurface();
                break;

            case SKTouchAction.Moved:
                if (_draggingIndex < 0) break;
                UpdateDraggedPoint(e.Location, scale, offX, offY);
                long now = Environment.TickCount64;
                if (now - _lastRecomputeMs >= RecomputeThrottleMs)
                {
                    RecomputeWarp();
                    _lastRecomputeMs = now;
                }
                InvalidateSurface(); // the dragged dot itself redraws every event even when the throttle above skips the expensive re-warp
                break;

            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                if (_draggingIndex >= 0) RecomputeWarp(); // one final, un-throttled pass so the release frame is always sharp, not whatever stale frame the throttle last allowed through
                _draggingIndex = -1;
                InvalidateSurface();
                break;
        }
        e.Handled = true;
    }

    private int NearestPointIndex(SKPoint touch, float scale, float offX, float offY)
    {
        int best = -1;
        float bestDist = HitRadiusPx;
        for (int row = 0; row < _grid.Rows; row++)
        {
            for (int col = 0; col < _grid.Columns; col++)
            {
                SKPoint p = PointCanvasPos(col, row, scale, offX, offY);
                float dist = MathF.Sqrt(MathF.Pow(p.X - touch.X, 2) + MathF.Pow(p.Y - touch.Y, 2));
                if (dist < bestDist) { bestDist = dist; best = row * _grid.Columns + col; }
            }
        }
        return best;
    }

    private void UpdateDraggedPoint(SKPoint touch, float scale, float offX, float offY)
    {
        if (_previewSource == null) return;
        int col = _draggingIndex % _grid.Columns;
        int row = _draggingIndex / _grid.Columns;
        double restX = (double)col / Math.Max(_grid.Columns - 1, 1);
        double restY = (double)row / Math.Max(_grid.Rows - 1, 1);
        double touchNx = (touch.X - offX) / (scale * _previewSource.Width);
        double touchNy = (touch.Y - offY) / (scale * _previewSource.Height);
        // Clamp: a displacement far outside this range just samples fully-transparent pixels (see
        // MeshWarp.Apply's own remarks on out-of-bounds sampling) -- bounding it keeps every drag visually
        // meaningful instead of letting a point wander off into "nothing happens here anymore".
        double dx = Math.Clamp(touchNx - restX, -0.45, 0.45);
        double dy = Math.Clamp(touchNy - restY, -0.45, 0.45);
        _displacements[_draggingIndex] = (dx, dy);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_previewSource == null) return;
        (float scale, float offX, float offY) = FitTransform();

        SKBitmap display = _warped ?? _previewSource;
        canvas.Save();
        canvas.Translate(offX, offY);
        canvas.Scale(scale);
        using (SKImage image = SKImage.FromBitmap(display)) canvas.DrawImage(image, 0, 0);
        canvas.Restore();

        using var linePaint = new SKPaint { Color = GridColor.WithAlpha(160), StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        for (int row = 0; row < _grid.Rows; row++)
            for (int col = 0; col < _grid.Columns - 1; col++)
                canvas.DrawLine(PointCanvasPos(col, row, scale, offX, offY), PointCanvasPos(col + 1, row, scale, offX, offY), linePaint);
        for (int col = 0; col < _grid.Columns; col++)
            for (int row = 0; row < _grid.Rows - 1; row++)
                canvas.DrawLine(PointCanvasPos(col, row, scale, offX, offY), PointCanvasPos(col, row + 1, scale, offX, offY), linePaint);

        using var outlinePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        for (int row = 0; row < _grid.Rows; row++)
        {
            for (int col = 0; col < _grid.Columns; col++)
            {
                int index = row * _grid.Columns + col;
                SKPoint p = PointCanvasPos(col, row, scale, offX, offY);
                bool active = index == _draggingIndex;
                float radius = active ? 10f : 7f;
                using var dotPaint = new SKPaint { Color = active ? ActivePointColor : GridColor, IsAntialias = true, Style = SKPaintStyle.Fill };
                canvas.DrawCircle(p, radius, dotPaint);
                canvas.DrawCircle(p, radius, outlinePaint);
            }
        }
    }
}
