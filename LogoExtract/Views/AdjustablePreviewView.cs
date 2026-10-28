using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace LogoExtract.Views;

/// <summary>What <see cref="AdjustablePreviewView"/> draws behind the (possibly transparent) cutout, so the
/// user can judge edge/alpha quality against more than one backdrop -- added 2026-10-27, owner's report: a
/// fixed translucent gray backing hides both near-white and near-black fringing. Checkerboard is the default
/// -- the universal "this is transparency" convention every image editor uses -- with solid White/Black (the
/// two most common real-world placement backgrounds) and the original Gray kept as a 4th option.</summary>
public enum PreviewBackgroundKind { Checkerboard, White, Black, Gray }

/// <summary>EditPage's color-adjusted cutout preview, with pinch-zoom/pan -- raw SkiaSharp canvas + hand-rolled
/// touch handling, the same shape as <see cref="MaskEditorView"/>'s own zoom (SegmentPage), NOT MAUI's
/// PinchGestureRecognizer / View.Scale+TranslationX/Y. Owner's report, 2026-10-27: "zoom vẫn bị giật lag ...
/// so sánh với các view image ở màn khác ... zoom rất mượt" -- after isolating the transform onto its own
/// wrapper Grid still didn't fix it, pointing at MAUI's own gesture-recognizer/property-binding pipeline on
/// Android as the real cost, not the color filter. This view sidesteps that pipeline entirely, the same way
/// MaskEditorView already does successfully in this app.
///
/// <para>Brightness/Contrast still apply as a zero-CPU-per-frame color matrix, not a re-filtered bitmap: built
/// once into an <see cref="SKColorFilter"/> on this view's paint (<see cref="BuildColorMatrix"/> is the exact
/// same 0..255, row-major 4x5 matrix math the old Android-native ColorMatrixColorFilter used, since Android's
/// own ColorMatrixColorFilter is itself a thin wrapper over this same Skia primitive). The full-resolution
/// adjusted bitmap for Export/SaveToGallery is still baked separately, on demand, by
/// EditViewModel.ComputeFullResAdjustedAsync.</para></summary>
public sealed class AdjustablePreviewView : SKCanvasView
{
    public static readonly BindableProperty SourceBitmapProperty =
        BindableProperty.Create(nameof(SourceBitmap), typeof(SKBitmap), typeof(AdjustablePreviewView), null, propertyChanged: OnSourceBitmapChanged);

    public static readonly BindableProperty PreviewBackgroundProperty =
        BindableProperty.Create(nameof(PreviewBackground), typeof(PreviewBackgroundKind), typeof(AdjustablePreviewView),
            PreviewBackgroundKind.Checkerboard, propertyChanged: (b, _, _) => ((AdjustablePreviewView)b).InvalidateSurface());

    public static readonly BindableProperty BrightnessProperty =
        BindableProperty.Create(nameof(Brightness), typeof(double), typeof(AdjustablePreviewView), 0.0, propertyChanged: OnAdjustChanged);

    public static readonly BindableProperty ContrastProperty =
        BindableProperty.Create(nameof(Contrast), typeof(double), typeof(AdjustablePreviewView), 0.0, propertyChanged: OnAdjustChanged);

    public SKBitmap? SourceBitmap
    {
        get => (SKBitmap?)GetValue(SourceBitmapProperty);
        set => SetValue(SourceBitmapProperty, value);
    }

    public double Brightness
    {
        get => (double)GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public double Contrast
    {
        get => (double)GetValue(ContrastProperty);
        set => SetValue(ContrastProperty, value);
    }

    /// <summary>What this view paints behind the image, before the image itself -- see <see cref="PreviewBackgroundKind"/>.</summary>
    public PreviewBackgroundKind PreviewBackground
    {
        get => (PreviewBackgroundKind)GetValue(PreviewBackgroundProperty);
        set => SetValue(PreviewBackgroundProperty, value);
    }

    private SKBitmap? _bitmap;

    private const float MinZoom = 1f;
    private const float MaxZoom = 6f;
    private float _zoom = 1f;
    private float _panX, _panY;
    private readonly Dictionary<long, SKPoint> _activeTouches = [];

    private SKPoint _panStartTouch;
    private SKPoint _panStartOffset;

    private float _pinchStartDistance;
    private float _pinchStartZoom;
    private SKPoint _pinchStartMidpoint;
    private SKPoint _pinchStartPan;

    // Hand-rolled double-tap-to-reset detection -- not a MAUI TapGestureRecognizer layered on top of this view's
    // own raw touch handling, which would risk exactly the "gestures fighting each other" class of bug that
    // motivated moving off MAUI gestures in the first place.
    private const long DoubleTapMs = 300;
    private const float TapSlopPx = 40;
    private long _lastTapTimestamp = -DoubleTapMs - 1;
    private SKPoint _lastTapLocation;
    private SKPoint _pressLocation;
    private long _pressTimestamp;

    public AdjustablePreviewView()
    {
        EnableTouchEvents = true;
        Touch += OnTouch;
        PaintSurface += OnPaintSurface;
    }

    private static void OnSourceBitmapChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (AdjustablePreviewView)bindable;
        view._bitmap = newValue as SKBitmap;
        view._zoom = 1f;
        view._panX = 0f;
        view._panY = 0f;
        view.InvalidateSurface();
    }

    private static void OnAdjustChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((AdjustablePreviewView)bindable).InvalidateSurface();

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        if (_bitmap == null) { e.Handled = true; return; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _activeTouches[e.Id] = e.Location;
                if (_activeTouches.Count == 1)
                {
                    _pressLocation = e.Location;
                    _pressTimestamp = now;
                    _panStartTouch = e.Location;
                    _panStartOffset = new SKPoint(_panX, _panY);
                }
                else
                {
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
                    ClampPan();
                    InvalidateSurface();
                }
                else if (_zoom > 1f)
                {
                    _panX = _panStartOffset.X + (e.Location.X - _panStartTouch.X);
                    _panY = _panStartOffset.Y + (e.Location.Y - _panStartTouch.Y);
                    ClampPan();
                    InvalidateSurface();
                }
                break;

            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                _activeTouches.Remove(e.Id);
                if (e.ActionType == SKTouchAction.Released && _activeTouches.Count == 0)
                {
                    bool quickTap = now - _pressTimestamp < 250 && Distance(e.Location, _pressLocation) < TapSlopPx;
                    if (quickTap)
                    {
                        bool isDoubleTap = now - _lastTapTimestamp < DoubleTapMs && Distance(e.Location, _lastTapLocation) < TapSlopPx;
                        if (isDoubleTap)
                        {
                            _zoom = 1f;
                            _panX = 0f;
                            _panY = 0f;
                            InvalidateSurface();
                            _lastTapTimestamp = -DoubleTapMs - 1;
                        }
                        else
                        {
                            _lastTapTimestamp = now;
                            _lastTapLocation = e.Location;
                        }
                    }
                }
                break;
        }
        e.Handled = true;
    }

    private void ClampPan()
    {
        if (_bitmap == null || CanvasSize.Width <= 0 || CanvasSize.Height <= 0) return;
        float baseScale = Math.Min(CanvasSize.Width / _bitmap.Width, CanvasSize.Height / _bitmap.Height);
        float scale = baseScale * _zoom;
        float scaledW = _bitmap.Width * scale;
        float scaledH = _bitmap.Height * scale;
        float maxPanX = Math.Max(0, (scaledW - CanvasSize.Width) / 2);
        float maxPanY = Math.Max(0, (scaledH - CanvasSize.Height) / 2);
        _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
        _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2));
    private static SKPoint Midpoint(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        DrawBackground(canvas, e.Info.Width, e.Info.Height);
        if (_bitmap == null || CanvasSize.Width <= 0 || CanvasSize.Height <= 0) return;

        float baseScale = Math.Min(CanvasSize.Width / _bitmap.Width, CanvasSize.Height / _bitmap.Height);
        float scale = baseScale * _zoom;
        float offX = (CanvasSize.Width - _bitmap.Width * scale) / 2 + _panX;
        float offY = (CanvasSize.Height - _bitmap.Height * scale) / 2 + _panY;

        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(BuildColorMatrix(Brightness, Contrast)) };
        canvas.Save();
        canvas.Translate(offX, offY);
        canvas.Scale(scale);
        using (SKImage image = SKImage.FromBitmap(_bitmap)) canvas.DrawImage(image, 0, 0, paint);
        canvas.Restore();
    }

    private void DrawBackground(SKCanvas canvas, int width, int height)
    {
        switch (PreviewBackground)
        {
            case PreviewBackgroundKind.White:
                canvas.Clear(SKColors.White);
                break;
            case PreviewBackgroundKind.Black:
                canvas.Clear(SKColors.Black);
                break;
            case PreviewBackgroundKind.Gray:
                canvas.Clear(new SKColor(128, 128, 128));
                break;
            case PreviewBackgroundKind.Checkerboard:
            default:
                DrawCheckerboard(canvas, width, height);
                break;
        }
    }

    /// <summary>The universal "this is transparency" pattern every image editor uses -- added 2026-10-27 so the
    /// user can actually judge a cutout's edge/alpha quality (a flat gray backing, the old fixed behavior,
    /// hides both near-white and near-black fringing equally well, which is to say it hides it either way).
    /// Cell size is a raw canvas-pixel constant, same convention as this file's own zoom/pan math and
    /// MaskEditorView's brush radius -- this codebase doesn't density-correct raw Skia drawing elsewhere either.</summary>
    private static void DrawCheckerboard(SKCanvas canvas, int width, int height)
    {
        const float cell = 24f;
        using var light = new SKPaint { Color = new SKColor(238, 238, 238), Style = SKPaintStyle.Fill };
        using var dark = new SKPaint { Color = new SKColor(204, 204, 204), Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(0, 0, width, height), light);
        for (float y = 0; y < height; y += cell)
        {
            for (float x = 0; x < width; x += cell)
            {
                bool isDark = ((int)(x / cell) + (int)(y / cell)) % 2 == 1;
                if (isDark) canvas.DrawRect(new SKRect(x, y, Math.Min(x + cell, width), Math.Min(y + cell, height)), dark);
            }
        }
    }

    /// <summary>Same linear transform as LogoExtract.Core.Imaging.ColorAdjustFilter's per-channel LUT (contrast
    /// in -1..1 maps to a 0..∞ multiplicative scale via the same tan() curve), expressed as a 4x5 color matrix
    /// instead of a lookup table, so the two previews produce identical-looking results.
    ///
    /// <para>Owner's report, 2026-10-28: "kéo giảm sáng nó bị đen hình, tăng sáng nó bị trắng hình... tương phản
    /// cũng lỗi tương tự" -- SkiaSharp's <see cref="SKColorFilter.CreateColorMatrix"/> operates on UN-scaled 0..1
    /// channel values (native Skia's own convention), unlike android.graphics.ColorMatrix's 0..255 convention
    /// this was first ported from. The previous version fed it an offset on the 0..255 scale (up to ±255,
    /// contrast's own 0..128 term included) -- hundreds of times too large for a channel that clamps at 1.0, so
    /// almost any slider movement instantly saturated every pixel to solid black or white. Fixed by doing the
    /// exact same (c - 0.5) * contrast + 0.5 + brightness shift ColorAdjustFilter's LUT does on byte values
    /// 0..255, just pre-divided by 255 so it lands in the 0..1 range this API actually expects.</para>
    ///
    /// White balance is intentionally NOT included here: EditPage has no white-balance control yet (PLAN.md 2.4,
    /// not built), so ColorAdjust.WhiteBalance is always its default (1,1,1) in this app today.</summary>
    private static float[] BuildColorMatrix(double brightnessNorm, double contrastNorm)
    {
        float brightness = (float)brightnessNorm; // already -1..1 -- a full-scale shift on a 0..1 channel
        float contrast = (float)Math.Tan((contrastNorm + 1) * Math.PI / 4);
        float offset = 0.5f - 0.5f * contrast + brightness;
        return
        [
            contrast, 0, 0, 0, offset,
            0, contrast, 0, 0, offset,
            0, 0, contrast, 0, offset,
            0, 0, 0, 1, 0
        ];
    }
}
