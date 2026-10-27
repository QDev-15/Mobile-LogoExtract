using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Segmentation;
using LogoExtract.Services;
using LogoExtract.Views;
using SkiaSharp;

namespace LogoExtract.ViewModels;

[QueryProperty(nameof(ItemId), "itemId")]
public partial class SegmentViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private readonly AppDialog _dialog;
    // Lazy<T>, resolved from inside LoadAsync's Task.Run (see MauiProgram's own comment on this
    // registration): first access builds the ONNX Runtime session, real I/O + CPU work that must never run on
    // the UI thread during page construction/navigation.
    private readonly Lazy<ISegmentationService> _segmentation;
    private LogoItem? _item;
    private SKBitmap? _image;
    private SKBitmap? _mask;
    // Bumped by StartRegionSelect to invalidate whatever auto-segment/region-resegment is currently running: the
    // ONNX call inside RunAutoSegmentAsync/ConfirmRegionSelectAsync has no cancellation hook (InferenceSession.Run
    // is one atomic blocking call), so this can't actually abort it mid-inference -- it only makes that call's
    // result check this counter when it finishes and discard itself instead of overwriting the region-select
    // state the user has since moved into. Owner's report, 2026-10-27: tapping "Chọn vùng" while "Đang tách
    // nền..." was still running did nothing, because StartRegionSelect used to require a non-null _mask that
    // only existed once segmentation finished.
    private int _generation;

    public SegmentViewModel(ItemStore store, Lazy<ISegmentationService> segmentation, AppDialog dialog)
    {
        _store = store;
        _segmentation = segmentation;
        _dialog = dialog;
    }

    [ObservableProperty]
    private string itemId = "";

    [ObservableProperty]
    private bool isBusy = true;

    [ObservableProperty]
    private string busyText = "Đang tách nền...";

    /// <summary>True while the user is brushing a rough region (not the final mask) for
    /// <see cref="ConfirmRegionSelectAsync"/> to re-run segmentation inside -- SegmentPage swaps its whole
    /// bottom toolbar on this (Thêm/Xoá/Xong vs Huỷ/Xác nhận vùng).</summary>
    [ObservableProperty]
    private bool isSelectingRegion;

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // EXIF-oriented decode (ExifOrientation.cs): a bare SKBitmap.Decode ignores the EXIF rotation tag, so a
            // camera photo taken upright would otherwise show sideways everywhere this image is used -- found in code
            // review 2026-10-27, no real device photo had actually been tested through this path yet to surface it.
            // Split out of the segmentation Task.Run (they used to be one combined call) specifically so _image is
            // set -- and StartRegionSelect becomes usable -- as soon as this fast decode finishes, without waiting
            // for segmentation (slow, and not preemptible -- see RunAutoSegmentAsync's own comment).
            SKBitmap image = await Task.Run(() => ExifOrientation.DecodeOriented(path));
            Android.Util.Log.Info("LogoExtractPerf", $"[Load] DecodeOriented: {sw.ElapsedMilliseconds}ms");
            _image = image;

            if (item.CutoutFile != null)
            {
                sw.Restart();
                // A previously-saved mask.png is already in the image's own (already-oriented) pixel space, so it
                // does not need -- and must not get -- EXIF handling again; only re-decode as a plain bitmap. If
                // that file is missing or unreadable despite CutoutFile being set (deleted/corrupted externally),
                // fall through to running segmentation fresh rather than handing the editor a null mask (silent dead
                // screen -- the original bug here, found in review, not observed on-device).
                // Forced to Rgba8888 for the same reason as ExifOrientation.DecodeOriented: SaveAsync's compositing
                // below reads this mask's bytes directly assuming that exact channel order.
                SKBitmap? savedMask = await Task.Run(() => BitmapIO.DecodeAsRgba8888(maskPath));
                Android.Util.Log.Info("LogoExtractPerf", $"[Load] savedMask check: {sw.ElapsedMilliseconds}ms");
                if (savedMask != null)
                {
                    _mask = savedMask;
                    IsBusy = false;
                    ImagesReady?.Invoke(image, savedMask);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Must never leave IsBusy stuck true forever -- see RunAutoSegmentAsync's own catch for why (same
            // silent-fire-and-forget-hang shape, just on the decode step instead of the ONNX call).
            Android.Util.Log.Error("LogoExtractPerf", $"[Load] decode FAILED: {ex}");
            IsBusy = false;
            await _dialog.AlertAsync("Lỗi", "Không thể mở ảnh.");
            return;
        }
        await RunAutoSegmentAsync();
    }

    /// <summary>Runs (or re-runs) auto-segmentation on the already-decoded <see cref="_image"/> and publishes the
    /// result -- unless <see cref="StartRegionSelect"/> bumped <see cref="_generation"/> while this was running,
    /// in which case the result is discarded instead of overwriting the region-select state the user moved into.
    /// Called from <see cref="LoadAsync"/> (first load, no saved mask) and from <see cref="CancelRegionSelect"/>/
    /// <see cref="ConfirmRegionSelectAsync"/> when region-select is abandoned before any real mask ever existed
    /// (it was interrupted before finishing the first time).</summary>
    private async Task RunAutoSegmentAsync()
    {
        if (_image == null) return;
        SKBitmap image = _image;
        int generation = _generation;
        IsBusy = true;
        BusyText = "Đang tách nền...";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SKBitmap mask;
        try
        {
            mask = await Task.Run(() => _segmentation.Value.Segment(image).AlphaMask);
        }
        catch (Exception ex)
        {
            // Must never leave IsBusy stuck true forever (owner's report, 2026-10-27: a Release/AAB build
            // installed on a real device "spins as if hung" during segmentation). This Task.Run had no catch, so
            // a Release-only failure here -- e.g. AndroidLinkMode=Full's IL trimming removing something ONNX
            // Runtime's C# bindings reach only via reflection, a risk this project's own PdfReader.csproj already
            // documents for that same setting -- threw unobserved on this fire-and-forget call and silently froze
            // the busy spinner forever instead of surfacing anything. Logged at Error so a plain
            // `adb logcat -s LogoExtractPerf:*` catches it instead of needing to scroll past Info noise.
            Android.Util.Log.Error("LogoExtractPerf", $"[Load] segmentation FAILED: {ex}");
            if (generation == _generation)
            {
                IsBusy = false;
                await _dialog.AlertAsync("Lỗi", "Không thể tách nền. Thử lại hoặc dùng \"Chọn vùng\" để tô tay.");
            }
            return;
        }
        Android.Util.Log.Info("LogoExtractPerf", $"[Load] segmentation total: {sw.ElapsedMilliseconds}ms");
        if (generation != _generation)
        {
            Android.Util.Log.Info("LogoExtractPerf", "[Load] segmentation result discarded -- region-select interrupted it");
            mask.Dispose();
            return;
        }
        _mask = mask;
        IsBusy = false;
        ImagesReady?.Invoke(image, mask);
    }

    [RelayCommand]
    private void SetAddMode() => Mode = MaskEditorView.BrushMode.Add;

    [RelayCommand]
    private void SetRemoveMode() => Mode = MaskEditorView.BrushMode.Remove;

    /// <summary>Owner's report, 2026-10-27: "tách logo ra áo, nó lại tách áo ra khỏi nền" -- u2netp is a
    /// salient-object model, so on a full photo the "object" it finds is the whole shirt (biggest, highest-
    /// contrast thing in frame), not the small printed logo on it. Hands the editor a blank mask in Add mode so
    /// the user can roughly brush just the logo; <see cref="ConfirmRegionSelectAsync"/> then crops the ORIGINAL
    /// photo to that brushed area and re-runs segmentation inside only that crop, where the logo itself is now
    /// the dominant/highest-contrast shape the model sees instead of the whole shirt.</summary>
    [RelayCommand]
    private void StartRegionSelect()
    {
        if (_image == null || IsSelectingRegion) return;
        // Interrupts whatever auto-segment/region-resegment is still running in the background (see _generation's
        // own field comment for why this can only discard that call's eventual result, not actually abort the
        // ONNX inference already in flight) -- region-select takes priority over waiting for it.
        _generation++;
        IsBusy = false;
        IsSelectingRegion = true;
        // Editor.Load (below, via ImagesReady) disposes whatever mask it was previously showing -- that is
        // THIS _mask field's own instance (Editor and the ViewModel have shared the same object since
        // LoadAsync). Clone it first so CancelRegionSelect still has a live bitmap to redisplay; the original
        // gets disposed by Editor.Load exactly as it always has for a mask change. _mask can legitimately still
        // be null here (interrupted before segmentation ever finished once) -- CancelRegionSelect/
        // ConfirmRegionSelectAsync both handle that case by re-running segmentation instead of redisplaying.
        if (_mask != null) _mask = CloneBitmap(_mask);
        var blank = new SKBitmap(_image.Width, _image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas c = new(blank)) c.Clear(new SKColor(0, 0, 0, 255)); // R=0 everywhere = "not selected" (R channel carries the mask value, see MaskEditorView)
        ImagesReady?.Invoke(_image, blank); // reuses the same Editor.Load wiring SegmentPage already has
    }

    /// <summary>Discards whatever was brushed in region-select mode and goes back to showing the last real
    /// mask (the auto result, or a previous region-select result) untouched -- or, if region-select interrupted
    /// the very first auto-segment before it ever finished (so there is no real mask to go back to), re-runs it
    /// for real this time instead of leaving the editor on a stale blank mask.</summary>
    public void CancelRegionSelect()
    {
        if (_image == null) return;
        IsSelectingRegion = false;
        if (_mask != null) ImagesReady?.Invoke(_image, _mask);
        else _ = RunAutoSegmentAsync();
    }

    /// <summary>Takes ownership of <paramref name="selectionMask"/> (disposed here, same as
    /// <see cref="SaveAsync"/>'s own contract for the mask it's handed). An empty selection (user hit Xác nhận
    /// without brushing anything) falls back to just redisplaying the existing mask -- "không có vùng chọn tay
    /// thì làm như hiện tại" (owner's own fallback rule, 2026-10-27), already satisfied since nothing changes.</summary>
    public async Task ConfirmRegionSelectAsync(SKBitmap selectionMask)
    {
        if (_image == null) { selectionMask.Dispose(); return; }
        SKRectI? bounds = NonZeroBounds(selectionMask);
        selectionMask.Dispose();
        if (bounds == null)
        {
            IsSelectingRegion = false;
            if (_mask != null) ImagesReady?.Invoke(_image, _mask);
            else _ = RunAutoSegmentAsync(); // same "nothing to fall back to" case as CancelRegionSelect
            return;
        }

        int generation = _generation;
        IsBusy = true;
        BusyText = "Đang tách theo vùng đã chọn...";
        SKBitmap image = _image;
        // 20% padding around the brushed box: brushing is necessarily rough (finger-width circles), so this
        // keeps a sliver of surrounding fabric in the crop rather than slicing straight through the logo's own
        // edge if the user's strokes ran a little tight.
        SKRectI cropRect = PadAndClamp(bounds.Value, image.Width, image.Height, 0.20f);
        SKBitmap newMask;
        try
        {
            newMask = await Task.Run(() =>
            {
                using SKBitmap cropped = CropBitmap(image, cropRect);
                using SKBitmap croppedMask = _segmentation.Value.Segment(cropped).AlphaMask;
                return CompositeIntoFullSize(croppedMask, cropRect, image.Width, image.Height);
            });
        }
        catch (Exception ex)
        {
            // Same never-leave-IsBusy-stuck-forever reasoning as RunAutoSegmentAsync's own catch.
            Android.Util.Log.Error("LogoExtractPerf", $"[Load] region re-segment FAILED: {ex}");
            if (generation == _generation)
            {
                IsBusy = false;
                IsSelectingRegion = false;
                await _dialog.AlertAsync("Lỗi", "Không thể tách theo vùng đã chọn.");
            }
            return;
        }
        if (generation != _generation) { newMask.Dispose(); return; } // StartRegionSelect fired again before this finished -- see _generation's own comment
        IsBusy = false;
        IsSelectingRegion = false;
        _mask?.Dispose();
        _mask = newMask;
        ImagesReady?.Invoke(_image, _mask);
    }

    /// <summary>Scans the R channel (mask value, see MaskEditorView's own field comment) for the bounding box
    /// of everything the user brushed; null if nothing was brushed at all.</summary>
    private static SKRectI? NonZeroBounds(SKBitmap mask)
    {
        int minX = mask.Width, minY = mask.Height, maxX = -1, maxY = -1;
        using SKPixmap px = mask.PeekPixels();
        ReadOnlySpan<byte> span = px.GetPixelSpan();
        int stride = px.RowBytes;
        for (int y = 0; y < mask.Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < mask.Width; x++)
            {
                if (span[row + x * 4] <= 10) continue; // small threshold: ignore antialiased brush-edge noise
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < minX || maxY < minY ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static SKRectI PadAndClamp(SKRectI rect, int imageWidth, int imageHeight, float paddingRatio)
    {
        int padX = (int)(rect.Width * paddingRatio);
        int padY = (int)(rect.Height * paddingRatio);
        return new SKRectI(
            Math.Max(0, rect.Left - padX),
            Math.Max(0, rect.Top - padY),
            Math.Min(imageWidth, rect.Right + padX),
            Math.Min(imageHeight, rect.Bottom + padY));
    }

    private static SKBitmap CloneBitmap(SKBitmap source)
    {
        var copy = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using SKCanvas canvas = new(copy);
        canvas.DrawBitmap(source, 0, 0);
        return copy;
    }

    private static SKBitmap CropBitmap(SKBitmap source, SKRectI rect)
    {
        var dest = new SKBitmap(rect.Width, rect.Height, source.ColorType, source.AlphaType);
        source.ExtractSubset(dest, rect);
        return dest;
    }

    /// <summary>Places <paramref name="croppedMask"/> (the model's result on just the cropped region) back at
    /// its original position in a full-photo-sized mask; everywhere outside the crop is R=0/alpha=255 -- "not
    /// kept", matching every other mask this app produces (see MaskEditorView's field comment on the format)
    /// -- so the user's region choice doubles as a hard guarantee that nothing outside it survives, not just a
    /// hint to the model.</summary>
    private static SKBitmap CompositeIntoFullSize(SKBitmap croppedMask, SKRectI cropRect, int fullWidth, int fullHeight)
    {
        var full = new SKBitmap(fullWidth, fullHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(full);
        canvas.Clear(new SKColor(0, 0, 0, 255));
        canvas.DrawBitmap(croppedMask, cropRect.Left, cropRect.Top);
        return full;
    }

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

            // Color decontamination BEFORE compositing (owner's report, 2026-10-27: "viền trắng nhìn ảo quá"):
            // without this, a soft-alpha edge pixel keeps its full original color (a blend of logo + fabric),
            // which leaks the old fabric color through as a halo once composited onto anything else. See
            // ColorDecontamination's own remarks for the full reasoning.
            using SKBitmap decontaminated = ColorDecontamination.Decontaminate(_image, editedMask);

            // Composite via raw pixel spans, not GetPixel/SetPixel per pixel (review 2026-10-27): a real phone
            // photo is routinely 12+ MP, and GetPixel/SetPixel's per-call overhead made this loop visibly slow
            // (multi-second) on a photo that size -- span access turns it into a tight byte-copy loop instead.
            using var cutout = new SKBitmap(_image.Width, _image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (SKPixmap imagePx = decontaminated.PeekPixels())
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
