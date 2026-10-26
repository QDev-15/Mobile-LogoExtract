using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>SKBitmap.Decode(path/stream) does NOT apply EXIF orientation -- it returns the raw, as-stored pixel
/// grid and leaves the orientation tag for the caller to handle via SKCodec.EncodedOrigin. Found in code review
/// 2026-10-27: nothing in this app was reading that tag at all, so any photo with EXIF rotation (the overwhelming
/// majority of phone camera photos, especially ones taken holding the phone upright) would show sideways or
/// upside down everywhere a decoded bitmap is displayed or processed. Use <see cref="DecodeOriented"/> instead
/// of a bare SKBitmap.Decode wherever a photo comes from the camera or a picked file.</summary>
public static class ExifOrientation
{
    /// <summary>Always returns Rgba8888 (never whatever platform-default color type a bare decode would give --
    /// Android's is commonly Bgra8888), specifically so callers elsewhere in this codebase that index pixel
    /// bytes directly (SegmentViewModel's cutout compositing, MaskEditorView's overlay cache) can assume a
    /// fixed R-G-B-A byte order without each one re-checking/converting. Found in review 2026-10-27 while
    /// writing that span-based compositing: without forcing this, it would have silently swapped the red and
    /// blue channels on any device whose native decode format isn't RGBA.</summary>
    public static SKBitmap DecodeOriented(string path)
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"Không đọc được ảnh: {path}");
        SKImageInfo info = codec.Info.WithColorType(SKColorType.Rgba8888);
        using SKBitmap raw = SKBitmap.Decode(codec, info) ?? throw new InvalidOperationException($"Không đọc được ảnh: {path}");
        return Apply(raw, codec.EncodedOrigin);
    }

    /// <summary>Returns a NEW Rgba8888 bitmap with the orientation baked in (the caller owns disposing both the
    /// input and the result independently -- unlike SkiaSharp's own transform helpers, this never disposes or
    /// mutates <paramref name="raw"/>).</summary>
    public static SKBitmap Apply(SKBitmap raw, SKEncodedOrigin origin)
    {
        bool swapDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swapDimensions ? raw.Height : raw.Width;
        int height = swapDimensions ? raw.Width : raw.Height;
        var result = new SKBitmap(width, height, SKColorType.Rgba8888, raw.AlphaType);

        if (origin == SKEncodedOrigin.TopLeft)
        {
            using SKCanvas identityCanvas = new(result);
            identityCanvas.DrawBitmap(raw, 0, 0);
            return result;
        }

        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // flip horizontal
                canvas.Scale(-1, 1, width / 2f, 0);
                break;
            case SKEncodedOrigin.BottomRight: // rotate 180
                canvas.RotateDegrees(180, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft: // flip vertical
                canvas.Scale(1, -1, 0, height / 2f);
                break;
            case SKEncodedOrigin.LeftTop: // transpose
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop: // rotate 90 CW
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transverse
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftBottom: // rotate 270 CW (90 CCW)
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;
        }
        canvas.DrawBitmap(raw, 0, 0);
        return result;
    }
}
