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
    /// bitmaps) before adopting the new ones.</summary>
    public void Load(SKBitmap image, SKBitmap mask)
    {
        _image?.Dispose();
        _mask?.Dispose();
        _tintedOverlay?.Dispose();
        _image = image;
        _mask = mask;
        _tintedOverlay = BuildTintedOverlay(mask);
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

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        if (e.ActionType is not (SKTouchAction.Pressed or SKTouchAction.Moved) || _image == null || _mask == null || _tintedOverlay == null)
        {
            e.Handled = true;
            return;
        }
        (float scale, float offX, float offY) = FitTransform();
        float imgX = (e.Location.X - offX) / scale;
        float imgY = (e.Location.Y - offY) / scale;
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
        e.Handled = true;
    }

    private (float Scale, float OffsetX, float OffsetY) FitTransform()
    {
        if (_image == null || CanvasSize.Width <= 0 || CanvasSize.Height <= 0) return (1, 0, 0);
        float scale = Math.Min(CanvasSize.Width / _image.Width, CanvasSize.Height / _image.Height);
        float offX = (CanvasSize.Width - _image.Width * scale) / 2;
        float offY = (CanvasSize.Height - _image.Height * scale) / 2;
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

    /// <summary>Called by the page when it is navigated away from (see SegmentPage.xaml.cs's OnDisappearing):
    /// <see cref="_image"/>/<see cref="_mask"/>/<see cref="_tintedOverlay"/> are native-memory-backed and not
    /// reliably reclaimed by waiting for the GC to notice the managed wrapper is unreachable -- explicit
    /// disposal is the only way to free that memory promptly (found in review: with no disposal at all, each
    /// visit to this screen permanently leaked a full-resolution copy of the photo plus two mask-sized bitmaps).</summary>
    public void Unload()
    {
        _image?.Dispose();
        _mask?.Dispose();
        _tintedOverlay?.Dispose();
        _image = null;
        _mask = null;
        _tintedOverlay = null;
    }
}
