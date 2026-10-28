using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace LogoExtract.Views;

/// <summary>Shows the photo with the current alpha mask overlaid (green tint = kept, nothing = removed as
/// background) and lets the user brush the mask by touch (PLAN.md 2.2's "cọ thêm/bớt vùng chọn" -- the manual
/// touch-up tool that this whole app's segmentation guarantee actually leans on; see
/// <c>Core/Segmentation/ISegmentationService</c>'s own remarks on why the automatic suggestion alone is often
/// not enough). SKCanvasView (raw SkiaSharp canvas, not Microsoft.Maui.Graphics' ICanvas) + hand-rolled touch
/// handling, same shape as DocScanner's QuadEditor.
///
/// <para><b>Perf note (2026-10-27 review)</b>: the first version rebuilt the whole green-tint overlay, pixel by
/// pixel via GetPixel/SetPixel, on EVERY touch-move event -- fine on the 350x472 test photos, but on a real
/// phone photo (routinely 3000x4000+, 12+ MP) that is tens of megabytes of bitmap allocation and ~12 million
/// GetPixel/SetPixel interop calls PER FRAME during a drag gesture: this would visibly freeze or crash
/// (OutOfMemory from GC never catching up to the allocation rate) on a real device, not just "feel slow". Fixed
/// by maintaining <see cref="_tintedOverlay"/> as a cache built ONCE in <see cref="Load"/> (via a raw pixel-span
/// pass, not per-pixel GetPixel/SetPixel) and updated INCREMENTALLY per touch (draw only the brush-sized circle
/// that actually changed, with <see cref="SKBlendMode.Src"/> so repeated strokes replace rather than
/// re-accumulate alpha) -- repaint cost is now independent of photo resolution.</para></summary>
public sealed class MaskEditorView : SKCanvasView
{
    public enum BrushMode { Add, Remove }

    private static readonly SKColor KeptTint = new(40, 220, 150, 110);
    private static readonly SKColor RemovedTint = new(0, 0, 0, 0);

    private SKBitmap? _image;
    private SKBitmap? _mask; // Rgba8888: R=G=B=0..255 mask value (255 = kept), alpha always 255 (see U2NetSegmentationService's comment on why Gray8 was dropped)
    private SKBitmap? _tintedOverlay; // Rgba8888, premul: cached green-tint render of _mask, kept in sync incrementally (see class remarks)
    private BrushMode _mode = BrushMode.Add;
    private float _brushRadiusPx = 40;

    // Pinch-zoom + pan state (owner's request, 2026-10-27: "thêm chức năng zoom to thu nhỏ ... để chọn cho
    // chuẩn" -- brushing the small region-select box, or a fine mask edge, is hard to do accurately at
    // fit-to-screen scale on a real multi-MP photo). Applied on top of FitTransform's own fit-to-canvas scale,
    // not a replacement for it.
    private const float MinZoom = 1f;
    private const float MaxZoom = 6f;
    private float _zoom = 1f;
    private float _panX, _panY; // canvas-pixel offset, applied after fit+zoom
    private readonly Dictionary<long, SKPoint> _activeTouches = [];
    private bool _singleTouchDrawing;
    private float _pinchStartDistance;
    private float _pinchStartZoom;
    private SKPoint _pinchStartMidpoint;
    private SKPoint _pinchStartPan;

    public MaskEditorView()
    {
        EnableTouchEvents = true;
        Touch += OnTouch;
        PaintSurface += OnPaintSurface;
    }

    public void SetMode(BrushMode mode) => _mode = mode;
    public void SetBrushSize(float radiusPx) => _brushRadiusPx = radiusPx;

    /// <summary>Takes ownership of <paramref name="image"/> and <paramref name="mask"/>: disposes whatever this
    /// view was previously showing (a view can be reused across items via dependency-injected transient pages
    /// in some navigation patterns; without this, re-opening the segment screen leaked the previous item's
    /// bitmaps) before adopting the new ones. This is the ONLY place <see cref="_image"/>/<see cref="_mask"/>
    /// get disposed, deliberately: there is no separate Unload() that disposes them from the page's own
    /// OnDisappearing. They are the SAME SKBitmap objects EditViewModel keeps in its own fields (handed over by
    /// reference, never copied -- formerly SegmentViewModel's fields, before the segment+edit screens merged
    /// into one page, 2026-10-29), so a page-level Unload() used to dispose them on EVERY disappear, including
    /// the ordinary forward navigation from the old SegmentPage to the old EditPage, leaving that VM holding
    /// disposed-but-non-null references the moment the user came back and tapped "Chọn vùng và tách lại". Owner's
    /// report, 2026-10-27: a SIGSEGV crash in libSkiaSharp.so's sk_bitmap_get_info, from StartRegionSelect
    /// reading _image.Width/.Height on exactly that freed bitmap. Disposal ownership now belongs entirely to
    /// EditViewModel/this method's own replace-on-reload logic.</summary>
    public void Load(SKBitmap image, SKBitmap mask)
    {
        // ReferenceEquals check: EditViewModel's region-select flow (2026-10-27, relocated from the former
        // SegmentViewModel 2026-10-29) calls Load() a second/third
        // time for the SAME item with the SAME original-photo SKBitmap instance (only the mask changes). Without
        // this guard, `_image?.Dispose()` would dispose the very object the `image` parameter also points to,
        // then `_image = image` would adopt an already-disposed bitmap -- first paint after that throws
        // ObjectDisposedException. A genuinely new image (the normal first-load case) still gets disposed as before.
        bool samePhoto = ReferenceEquals(_image, image);
        if (!samePhoto) _image?.Dispose();
        _mask?.Dispose();
        _tintedOverlay?.Dispose();
        _image = image;
        _mask = mask;
        _tintedOverlay = BuildTintedOverlay(mask);
        // Only reset zoom/pan for a genuinely new photo -- switching into/out of region-select mode on the SAME
        // photo keeps whatever the user was already zoomed into, so they don't lose their place.
        if (!samePhoto) { _zoom = 1f; _panX = 0f; _panY = 0f; }
        InvalidateSurface();
    }

    private static SKBitmap BuildTintedOverlay(SKBitmap mask)
    {
        var overlay = new SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        // Raw pixel-span pass (one-time cost, only when a new item is opened), not GetPixel/SetPixel per pixel:
        // the latter's per-call interop overhead is the main reason the old per-touch full rebuild was slow
        // enough to matter even once, let alone dozens of times a second.
        using (SKPixmap srcPixmap = mask.PeekPixels())
        using (SKPixmap dstPixmap = overlay.PeekPixels())
        {
            ReadOnlySpan<byte> src = srcPixmap.GetPixelSpan();
            Span<byte> dst = dstPixmap.GetPixelSpan();
            for (int i = 0; i + 3 < src.Length; i += 4)
            {
                byte v = src[i]; // R channel carries the mask value (see field comment)
                byte a = (byte)(v * KeptTint.Alpha / 255);
                // Premul: RGB stored pre-multiplied by alpha/255.
                dst[i] = (byte)(KeptTint.Red * a / 255);
                dst[i + 1] = (byte)(KeptTint.Green * a / 255);
                dst[i + 2] = (byte)(KeptTint.Blue * a / 255);
                dst[i + 3] = a;
            }
        }
        return overlay;
    }

    /// <summary>The mask as the user has brushed it so far -- callers read this directly, no copy, since the
    /// caller owns disposing it (same bitmap passed into <see cref="Load"/>).</summary>
    public SKBitmap? CurrentMask => _mask;

    /// <summary>Tracks every active finger by its own touch Id (SkiaSharp gives each a stable Id across its
    /// Pressed/Moved/Released sequence) so 1 finger can draw while a 2nd simultaneously turns the gesture into a
    /// pinch, same as every other photo app's zoom gesture.</summary>
    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        if (_image == null || _mask == null || _tintedOverlay == null) { e.Handled = true; return; }

        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _activeTouches[e.Id] = e.Location;
                if (_activeTouches.Count == 1)
                {
                    _singleTouchDrawing = true;
                    DrawBrushAt(e.Location);
                }
                else
                {
                    // A 2nd (or 3rd+, ignored) finger just landed: abandon any single-finger draw in progress
                    // and start tracking a pinch from the first 2 fingers' current positions.
                    _singleTouchDrawing = false;
                    SKPoint[] pts = [.. _activeTouches.Values];
                    _pinchStartDistance = MathF.Max(Distance(pts[0], pts[1]), 1f);
                    _pinchStartZoom = _zoom;
                    _pinchStartMidpoint = Midpoint(pts[0], pts[1]);
                    _pinchStartPan = new SKPoint(_panX, _panY);
                }
                break;

            case SKTouchAction.Moved:
                if (!_activeTouches.ContainsKey(e.Id)) break;
                _activeTouches[e.Id] = e.Location;
                if (_activeTouches.Count >= 2)
                {
                    SKPoint[] pts = [.. _activeTouches.Values];
                    float distance = Distance(pts[0], pts[1]);
                    _zoom = Math.Clamp(_pinchStartZoom * (distance / _pinchStartDistance), MinZoom, MaxZoom);
                    SKPoint mid = Midpoint(pts[0], pts[1]);
                    _panX = _pinchStartPan.X + (mid.X - _pinchStartMidpoint.X);
                    _panY = _pinchStartPan.Y + (mid.Y - _pinchStartMidpoint.Y);
                    InvalidateSurface();
                }
                else if (_singleTouchDrawing)
                {
                    DrawBrushAt(e.Location);
                }
                break;

            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                _activeTouches.Remove(e.Id);
                // Dropping from 2 fingers to 1 (pinch ending) must NOT resume drawing with the remaining
                // finger -- it's wherever it happened to be mid-pinch, not a deliberate brush stroke start.
                // _singleTouchDrawing only turns true again from a fresh Pressed with exactly 1 finger down.
                if (_activeTouches.Count == 0) _singleTouchDrawing = false;
                break;
        }
        e.Handled = true;
    }

    private void DrawBrushAt(SKPoint location)
    {
        (float scale, float offX, float offY) = FitTransform();
        if (scale <= 0 || _mask == null || _tintedOverlay == null) return;
        float imgX = (location.X - offX) / scale;
        float imgY = (location.Y - offY) / scale;
        float radiusInImagePx = _brushRadiusPx / scale;

        bool add = _mode == BrushMode.Add;
        using (var canvas = new SKCanvas(_mask))
        using (var paint = new SKPaint { Color = add ? new SKColor(255, 255, 255) : new SKColor(0, 0, 0), IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawCircle(imgX, imgY, radiusInImagePx, paint);

        // Same circle, same spot, onto the cached overlay -- SKBlendMode.Src REPLACES the destination pixels
        // instead of alpha-blending onto them, so brushing back and forth over the same spot doesn't
        // progressively darken/lighten it (a plain SrcOver draw would, since each stroke would layer onto the
        // previous one rather than reset it).
        using (var overlayCanvas = new SKCanvas(_tintedOverlay))
        using (var overlayPaint = new SKPaint { Color = add ? KeptTint : RemovedTint, IsAntialias = true, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Src })
            overlayCanvas.DrawCircle(imgX, imgY, radiusInImagePx, overlayPaint);

        InvalidateSurface();
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2));
    private static SKPoint Midpoint(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>Fit-to-canvas scale/offset (unchanged from before pinch-zoom existed) with the user's current
    /// zoom/pan applied on top -- at _zoom=1/_panX=_panY=0 this is exactly the old fit-to-screen behavior.</summary>
    private (float Scale, float OffsetX, float OffsetY) FitTransform()
    {
        if (_image == null || CanvasSize.Width <= 0 || CanvasSize.Height <= 0) return (1, 0, 0);
        float baseScale = Math.Min(CanvasSize.Width / _image.Width, CanvasSize.Height / _image.Height);
        float scale = baseScale * _zoom;
        float offX = (CanvasSize.Width - _image.Width * scale) / 2 + _panX;
        float offY = (CanvasSize.Height - _image.Height * scale) / 2 + _panY;
        return (scale, offX, offY);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_image == null || _tintedOverlay == null) return;
        (float scale, float offX, float offY) = FitTransform();

        canvas.Save();
        canvas.Translate(offX, offY);
        canvas.Scale(scale);
        using (SKImage skImage = SKImage.FromBitmap(_image)) canvas.DrawImage(skImage, 0, 0);
        using (SKImage tintedImage = SKImage.FromBitmap(_tintedOverlay)) canvas.DrawImage(tintedImage, 0, 0);
        canvas.Restore();
    }

}
