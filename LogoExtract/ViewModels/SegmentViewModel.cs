using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Segmentation;
using LogoExtract.Views;
using SkiaSharp;

namespace LogoExtract.ViewModels;

[QueryProperty(nameof(ItemId), "itemId")]
public partial class SegmentViewModel : ObservableObject
{
    private readonly ItemStore _store;
    // Lazy<T>, resolved from inside LoadAsync's Task.Run (see MauiProgram's own comment on this
    // registration): first access builds the ONNX Runtime session, real I/O + CPU work that must never run on
    // the UI thread during page construction/navigation.
    private readonly Lazy<ISegmentationService> _segmentation;
    private LogoItem? _item;
    private SKBitmap? _image;
    private SKBitmap? _mask;

    public SegmentViewModel(ItemStore store, Lazy<ISegmentationService> segmentation)
    {
        _store = store;
        _segmentation = segmentation;
    }

    [ObservableProperty]
    private string itemId = "";

    [ObservableProperty]
    private bool isBusy = true;

    [ObservableProperty]
    private string busyText = "Đang tách nền...";

    public event Action<SKBitmap, SKBitmap>? ImagesReady;

    public MaskEditorView.BrushMode Mode { get; private set; } = MaskEditorView.BrushMode.Add;

    partial void OnItemIdChanged(string value)
    {
        Android.Util.Log.Info("LogoExtractPerf", $"[Load] OnItemIdChanged: '{value}'");
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Android.Util.Log.Info("LogoExtractPerf", $"[Load] LoadAsync start, ItemId='{ItemId}'");
        LogoItem? item = _store.Get(ItemId);
        Android.Util.Log.Info("LogoExtractPerf", $"[Load] _store.Get returned: {(item == null ? "NULL" : item.Id)}");
        if (item == null) return;
        _item = item;
        string folder = _store.ItemFolder(item.Id);
        string path = Path.Combine(folder, item.OriginalFile);
        string maskPath = Path.Combine(folder, "mask.png");

        IsBusy = true;
        BusyText = "Đang tách nền...";
        Android.Util.Log.Info("LogoExtractPerf", "[Load] before Task.Run");
        (SKBitmap image, SKBitmap mask) = await Task.Run(() =>
        {
            Android.Util.Log.Info("LogoExtractPerf", "[Load] Task.Run started");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // EXIF-oriented decode (ExifOrientation.cs): a bare SKBitmap.Decode ignores the EXIF rotation tag,
            // so a camera photo taken upright would otherwise show sideways everywhere this image is used --
            // found in code review 2026-10-27, no real device photo had actually been tested through this path
            // yet to surface it.
            SKBitmap image = ExifOrientation.DecodeOriented(path);
            Android.Util.Log.Info("LogoExtractPerf", $"[Load] DecodeOriented: {sw.ElapsedMilliseconds}ms");
            sw.Restart();
            // A previously-saved mask.png is already in the image's own (already-oriented) pixel space, so it
            // does not need -- and must not get -- EXIF handling again; only re-decode as a plain bitmap. If
            // that file is missing or unreadable despite CutoutFile being set (deleted/corrupted externally),
            // fall back to running segmentation fresh rather than handing the editor a null mask (silent dead
            // screen -- the original bug here, found in review, not observed on-device).
            // Forced to Rgba8888 for the same reason as ExifOrientation.DecodeOriented: SaveAsync's compositing
            // below reads this mask's bytes directly assuming that exact channel order.
            SKBitmap? savedMask = item.CutoutFile != null ? BitmapIO.DecodeAsRgba8888(maskPath) : null;
            Android.Util.Log.Info("LogoExtractPerf", $"[Load] savedMask check: {sw.ElapsedMilliseconds}ms (CutoutFile={item.CutoutFile ?? "null"})");
            sw.Restart();
            Android.Util.Log.Info("LogoExtractPerf", "[Load] about to access _segmentation.Value");
            SKBitmap mask = savedMask ?? _segmentation.Value.Segment(image).AlphaMask;
            Android.Util.Log.Info("LogoExtractPerf", $"[Load] segmentation total: {sw.ElapsedMilliseconds}ms");
            return (image, mask);
        });
        Android.Util.Log.Info("LogoExtractPerf", "[Load] Task.Run returned");
        _image = image;
        _mask = mask;
        IsBusy = false;
        ImagesReady?.Invoke(image, mask);
    }

    [RelayCommand]
    private void SetAddMode() => Mode = MaskEditorView.BrushMode.Add;

    [RelayCommand]
    private void SetRemoveMode() => Mode = MaskEditorView.BrushMode.Remove;

    public async Task SaveAsync(SKBitmap editedMask)
    {
        if (_item == null || _image == null) return;
        IsBusy = true;
        BusyText = "Đang lưu...";
        string folder = _store.ItemFolder(_item.Id);
        await Task.Run(() =>
        {
            using (SKData maskData = editedMask.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream maskStream = File.Create(Path.Combine(folder, "mask.png")))
                maskData.SaveTo(maskStream);

            // Composite via raw pixel spans, not GetPixel/SetPixel per pixel (review 2026-10-27): a real phone
            // photo is routinely 12+ MP, and GetPixel/SetPixel's per-call overhead made this loop visibly slow
            // (multi-second) on a photo that size -- span access turns it into a tight byte-copy loop instead.
            using var cutout = new SKBitmap(_image.Width, _image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (SKPixmap imagePx = _image.PeekPixels())
            using (SKPixmap maskPx = editedMask.PeekPixels())
            using (SKPixmap cutoutPx = cutout.PeekPixels())
            {
                ReadOnlySpan<byte> imageBytes = imagePx.GetPixelSpan();
                ReadOnlySpan<byte> maskBytes = maskPx.GetPixelSpan();
                Span<byte> cutoutBytes = cutoutPx.GetPixelSpan();
                for (int i = 0; i + 3 < cutoutBytes.Length; i += 4)
                {
                    cutoutBytes[i] = imageBytes[i];
                    cutoutBytes[i + 1] = imageBytes[i + 1];
                    cutoutBytes[i + 2] = imageBytes[i + 2];
                    cutoutBytes[i + 3] = maskBytes[i]; // mask value lives in its R channel (see field comment on _mask)
                }
            }
            using (SKData cutoutData = cutout.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream cutoutStream = File.Create(Path.Combine(folder, "cutout.png")))
                cutoutData.SaveTo(cutoutStream);

            using SKBitmap thumb = cutout.Resize(new SKImageInfo(320, (int)(320.0 * cutout.Height / cutout.Width)), SKSamplingOptions.Default)!;
            using (SKData thumbData = thumb.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream thumbStream = File.Create(Path.Combine(folder, "thumb.png")))
                thumbData.SaveTo(thumbStream);
        });

        _store.Update(_item.Id, i => i with { State = ItemState.Segmented, CutoutFile = "cutout.png", ThumbFile = "thumb.png" });
        IsBusy = false;
        await Shell.Current.GoToAsync($"{nameof(Views.EditPage)}?itemId={_item.Id}");
    }
}
