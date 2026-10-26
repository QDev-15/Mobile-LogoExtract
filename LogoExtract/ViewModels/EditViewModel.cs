using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Vectorize;
using SkiaSharp;

namespace LogoExtract.ViewModels;

[QueryProperty(nameof(ItemId), "itemId")]
public partial class EditViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private LogoItem? _item;
    private SKBitmap? _cutout;
    private SKBitmap? _lastAdjusted; // the bitmap PreviewImageSource currently shows; kept so ExportAsync re-encodes the exact same pixels instead of recomputing
    private CancellationTokenSource? _previewDebounce;

    public EditViewModel(ItemStore store)
    {
        _store = store;
    }

    [ObservableProperty]
    private string itemId = "";

    [ObservableProperty]
    private ImageSource? previewImageSource;

    [ObservableProperty]
    private double brightness; // -1..1

    [ObservableProperty]
    private double contrast; // -1..1

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "";

    [ObservableProperty]
    private string? vectorPreviewPath;

    partial void OnItemIdChanged(string value) => Load();
    partial void OnBrightnessChanged(double value) => SchedulePreview();
    partial void OnContrastChanged(double value) => SchedulePreview();

    private void Load()
    {
        _cutout?.Dispose();
        _cutout = null;
        _item = _store.Get(ItemId);
        if (_item?.CutoutFile == null) return;
        string path = Path.Combine(_store.ItemFolder(_item.Id), _item.CutoutFile);
        _cutout = BitmapIO.DecodeAsRgba8888(path);
        if (_cutout == null) return; // corrupted/missing cutout file; leave the preview blank rather than crash on a null bitmap below
        Brightness = _item.ColorAdjust.Brightness;
        Contrast = _item.ColorAdjust.Contrast;
        SchedulePreview();
    }

    /// <summary>Debounces slider drags: review 2026-10-27 found the first version re-ran the full color-adjust
    /// pixel pass AND wrote a PNG to disk on every single ValueChanged tick while dragging (dozens per second),
    /// which on a real multi-MP photo would make the slider stutter badly and needlessly wear out flash storage.
    /// Now waits for a short pause in input, runs the pixel work off the UI thread, and updates the preview via
    /// an in-memory ImageSource (no disk write at all for the live preview -- only SaveColorAsync/ExportAsync
    /// touch disk).</summary>
    private void SchedulePreview()
    {
        _previewDebounce?.Cancel();
        _previewDebounce = new CancellationTokenSource();
        CancellationToken token = _previewDebounce.Token;
        _ = DebouncedApplyPreviewAsync(token);
    }

    private async Task DebouncedApplyPreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
        }
        catch (TaskCanceledException)
        {
            return; // superseded by a newer slider change
        }
        if (token.IsCancellationRequested || _cutout == null) return;

        SKBitmap cutout = _cutout; // local copy: safe to read off-thread, Load() only ever replaces the field on the UI thread between gestures
        var adjust = new ColorAdjust { Brightness = Brightness, Contrast = Contrast };
        (SKBitmap adjusted, byte[] encoded) = await Task.Run(() =>
        {
            SKBitmap result = ColorAdjustFilter.Apply(cutout, adjust);
            using SKData data = result.Encode(SKEncodedImageFormat.Png, 100);
            return (result, data.ToArray());
        }, token);
        if (token.IsCancellationRequested) { adjusted.Dispose(); return; }

        _lastAdjusted?.Dispose();
        _lastAdjusted = adjusted;
        PreviewImageSource = ImageSource.FromStream(() => new MemoryStream(encoded));
    }

    [RelayCommand]
    private async Task SaveColorAsync()
    {
        if (_item == null) return;
        var adjust = new ColorAdjust { Brightness = Brightness, Contrast = Contrast };
        _store.Update(_item.Id, i => i with { ColorAdjust = adjust });
        await Shell.Current.DisplayAlert("Đã lưu", "Đã lưu chỉnh màu.", "Đóng");
    }

    [RelayCommand]
    private async Task VectorizeAsync()
    {
        if (_item?.CutoutFile == null || _cutout == null) return;
        IsBusy = true;
        BusyText = "Đang vector hoá...";
        string folder = _store.ItemFolder(_item.Id);
        string svgPath = Path.Combine(folder, "vector.svg");
        int width = _cutout.Width, height = _cutout.Height;
        bool ok = await Task.Run(() =>
        {
            using SKBitmap? maskForTrace = BitmapIO.DecodeAsRgba8888(Path.Combine(folder, "mask.png"));
            if (maskForTrace == null) return false; // missing/corrupted mask.png -- real segmentation never finished right, don't claim success below
            IReadOnlyList<Contour> contours = ContourTracer.Trace(maskForTrace);
            string svg = ContourTracer.ToSvg(contours, width, height, "#1FAE82");
            File.WriteAllText(svgPath, svg);
            return true;
        });
        IsBusy = false;
        if (!ok)
        {
            await Shell.Current.DisplayAlert("Không vector hoá được", "Không tìm thấy dữ liệu tách nền. Hãy quay lại bước Tách nền trước.", "Đóng");
            return;
        }
        _store.Update(_item.Id, i => i with { State = ItemState.Vectorized, VectorFile = "vector.svg" });
        VectorPreviewPath = svgPath;
        await Shell.Current.DisplayAlert("Đã vector hoá",
            "Đã tạo file đường viền (cut path) SVG. Đây là bản v1: đường viền ngoài 1 màu, chưa phải vector tách theo từng vùng màu.",
            "Đóng");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_item == null) return;
        string folder = _store.ItemFolder(_item.Id);
        List<ShareFile> files = [];

        // Re-encode whatever PreviewImageSource is currently showing (the live-adjusted pixels, not necessarily
        // saved yet -- "what you see is what you get") to a temp file, since Share needs a real file path, not
        // the in-memory bytes already held for the preview.
        if (_lastAdjusted != null)
        {
            string exportPath = Path.Combine(folder, "export_preview.png");
            await Task.Run(() =>
            {
                using SKData data = _lastAdjusted.Encode(SKEncodedImageFormat.Png, 100);
                using FileStream stream = File.Create(exportPath);
                data.SaveTo(stream);
            });
            files.Add(new ShareFile(exportPath));
        }
        if (_item.VectorFile != null && File.Exists(Path.Combine(folder, _item.VectorFile))) files.Add(new ShareFile(Path.Combine(folder, _item.VectorFile)));
        if (files.Count == 0) return;
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest { Title = "Xuất logo", Files = files });
    }

    /// <summary>Called from EditPage's OnDisappearing -- see MaskEditorView.Unload's remarks on why native
    /// SKBitmap memory needs an explicit dispose rather than waiting on the GC.</summary>
    public void Unload()
    {
        _previewDebounce?.Cancel();
        _cutout?.Dispose();
        _cutout = null;
        _lastAdjusted?.Dispose();
        _lastAdjusted = null;
    }
}
