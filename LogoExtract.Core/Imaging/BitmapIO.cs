using SkiaSharp;

namespace LogoExtract.Core.Imaging;

/// <summary>Decoding helpers that always return Rgba8888, the one assumption every raw pixel-span operation in
/// this codebase (SegmentViewModel's compositing, ColorAdjustFilter, MaskEditorView's overlay cache) depends on.
/// A bare SKBitmap.Decode(path) instead returns whatever the platform's native/fastest format happens to be
/// (commonly Bgra8888 on Android) -- found in code review 2026-10-27 while adding the span-based compositing:
/// without forcing this everywhere a bitmap is decoded from disk, red and blue would silently swap on any
/// device whose native format isn't RGBA. See also ExifOrientation.DecodeOriented for camera/picked photos
/// specifically (which additionally need EXIF rotation applied); use THIS for anything this app wrote itself
/// (cutout.png, mask.png, preview files) where no EXIF handling is needed or wanted.</summary>
public static class BitmapIO
{
    public static SKBitmap? DecodeAsRgba8888(string path)
    {
        if (!File.Exists(path)) return null;
        using SKCodec? codec = SKCodec.Create(path);
        if (codec == null) return null;
        return SKBitmap.Decode(codec, codec.Info.WithColorType(SKColorType.Rgba8888));
    }
}
