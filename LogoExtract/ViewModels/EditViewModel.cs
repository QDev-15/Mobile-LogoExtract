using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Vectorize;
using LogoExtract.Services;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;

namespace LogoExtract.ViewModels;

[QueryProperty(nameof(ItemId), "itemId")]
public partial class EditViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private readonly AppDialog _dialog;
    private LogoItem? _item;
    private SKBitmap? _cutout; // full-resolution, loaded once from disk -- only ever re-filtered on demand (Export/SaveToGallery), never while dragging a slider

    public EditViewModel(ItemStore store, AppDialog dialog)
    {
        _store = store;
        _dialog = dialog;
    }

    [ObservableProperty]
    private string itemId = "";

    [ObservableProperty]
    private SKBitmap? previewBitmap;

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

    /// <summary>PreviewBitmap is decoded ONCE here, downscaled for on-screen use (see DownscaleForPreview's own
    /// remarks on why not full-resolution). Brightness/Contrast trigger no re-filter or re-encode at all:
    /// EditPage's AdjustablePreviewView is bound directly to both properties and applies them as a zero-CPU
    /// color matrix at draw time instead (see AdjustablePreviewView's own remarks). The full-resolution adjusted
    /// result for Export/SaveToGallery is still computed separately, on demand, by
    /// <see cref="ComputeFullResAdjustedAsync"/>.</summary>
    private void Load()
    {
        _cutout?.Dispose();
        _cutout = null;
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        _item = _store.Get(ItemId);
        if (_item?.CutoutFile == null) return;
        string path = Path.Combine(_store.ItemFolder(_item.Id), _item.CutoutFile);
        _cutout = BitmapIO.DecodeAsRgba8888(path);
        if (_cutout == null) return; // corrupted/missing cutout file; leave the preview blank rather than crash on a null bitmap below
        // Downscaled, not the full-resolution _cutout directly: a multi-MP bitmap (routinely 3000px+ on a side)
        // is unnecessary detail for a screen-sized preview and was part of what made zoom feel heavy before this
        // view moved to SkiaSharp-canvas-based zoom. ComputeFullResAdjustedAsync still reads full-resolution
        // _cutout separately for Export/SaveToGallery, so quality there is unaffected.
        PreviewBitmap = DownscaleForPreview(_cutout, 1400);
        Brightness = _item.ColorAdjust.Brightness;
        Contrast = _item.ColorAdjust.Contrast;
    }

    private static SKBitmap DownscaleForPreview(SKBitmap source, int maxDimension)
    {
        int longSide = Math.Max(source.Width, source.Height);
        if (longSide <= maxDimension) return source.Copy();
        float scale = maxDimension / (float)longSide;
        int width = Math.Max(1, (int)(source.Width * scale));
        int height = Math.Max(1, (int)(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
    }

    /// <summary>Computes the FULL-resolution color-adjusted cutout fresh, from <see cref="_cutout"/> -- called
    /// once by ExportAsync/SaveToGalleryAsync right when the user actually asks to export.</summary>
    private async Task<SKBitmap?> ComputeFullResAdjustedAsync()
    {
        if (_cutout == null) return null;
        SKBitmap cutout = _cutout;
        var adjust = new ColorAdjust { Brightness = Brightness, Contrast = Contrast };
        return await Task.Run(() => ColorAdjustFilter.Apply(cutout, adjust));
    }

    [RelayCommand]
    private async Task SaveColorAsync()
    {
        if (_item == null) return;
        var adjust = new ColorAdjust { Brightness = Brightness, Contrast = Contrast };
        _store.Update(_item.Id, i => i with { ColorAdjust = adjust });
        await _dialog.AlertAsync("Đã lưu", "Đã lưu chỉnh màu.", "Đóng");
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
            await _dialog.AlertAsync("Không vector hoá được", "Không tìm thấy dữ liệu tách nền. Hãy quay lại bước Tách nền trước.", "Đóng");
            return;
        }
        _store.Update(_item.Id, i => i with { State = ItemState.Vectorized, VectorFile = "vector.svg" });
        VectorPreviewPath = svgPath;
        await _dialog.AlertAsync("Đã vector hoá",
            "Đã tạo file đường viền (cut path) SVG. Đây là bản v1: đường viền ngoài 1 màu, chưa phải vector tách theo từng vùng màu.",
            "Đóng");
    }

    /// <summary>Saves the currently-previewed (color-adjusted) cutout straight into the device's public
    /// Pictures/LogoExtract folder via <see cref="GallerySaver"/> -- a direct save that needs no other app,
    /// as a reliable alternative to <see cref="ExportAsync"/>'s share sheet (owner's report, 2026-10-27:
    /// "xuất/chia sẻ ... không lưu được vào bộ nhớ máy" -- some share targets only preview the file or need
    /// sign-in/network, none guaranteed a local copy).</summary>
    [RelayCommand]
    private async Task SaveToGalleryAsync()
    {
        // WRITE_EXTERNAL_STORAGE is only declared (and only needed) up to API 28 (see AndroidManifest.xml);
        // from API 29 scoped storage lets any app write its own files into MediaStore without it.
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            PermissionStatus status = await Permissions.RequestAsync<Permissions.StorageWrite>();
            if (status != PermissionStatus.Granted)
            {
                await _dialog.AlertAsync("Thiếu quyền", "Cần quyền lưu trữ để lưu ảnh vào thư viện.", "Đóng");
                return;
            }
        }

        IsBusy = true;
        BusyText = "Đang lưu vào thư viện ảnh...";
        using SKBitmap? adjusted = await ComputeFullResAdjustedAsync();
        if (adjusted == null) { IsBusy = false; return; }
        string fileName = $"logoextract_{_item?.Name ?? "export"}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        string? saved = await Task.Run(() =>
        {
            using SKData data = adjusted.Encode(SKEncodedImageFormat.Png, 100);
            return GallerySaver.SaveImagePng(data.ToArray(), fileName);
        });
        IsBusy = false;

        await _dialog.AlertAsync(
            saved != null ? "Đã lưu" : "Lỗi",
            saved != null ? "Đã lưu ảnh vào thư viện ảnh (Pictures/LogoExtract)." : "Không lưu được ảnh vào thư viện ảnh.",
            "Đóng");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_item == null) return;
        string folder = _store.ItemFolder(_item.Id);
        List<ShareFile> files = [];

        // Full-resolution, computed fresh here (not read from a cached live-preview bitmap) so Share always
        // gets the current Brightness/Contrast at full quality, matching "what you see is what you get" even
        // though the on-screen preview itself only shows the adjustment via a GPU color matrix.
        using SKBitmap? adjusted = await ComputeFullResAdjustedAsync();
        if (adjusted != null)
        {
            string exportPath = Path.Combine(folder, "export_preview.png");
            await Task.Run(() =>
            {
                using SKData data = adjusted.Encode(SKEncodedImageFormat.Png, 100);
                using FileStream stream = File.Create(exportPath);
                data.SaveTo(stream);
            });
            files.Add(new ShareFile(exportPath));
        }
        if (_item.VectorFile != null && File.Exists(Path.Combine(folder, _item.VectorFile))) files.Add(new ShareFile(Path.Combine(folder, _item.VectorFile)));
        if (files.Count == 0) return;
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest { Title = "Xuất logo", Files = files });
    }

    /// <summary>Pops the whole navigation stack back to the dashboard in one step (Home -> Segment -> Edit
    /// otherwise takes 2 taps of the AppBar back arrow to leave) -- owner's request, 2026-10-27: a clear "done
    /// with this item" action belongs on this page once color/vector/export are finished, not just an implicit
    /// back button.</summary>
    [RelayCommand]
    private async Task FinishAsync() => await Shell.Current.GoToAsync("//HomePage");

    /// <summary>Called from EditPage's OnDisappearing -- see MaskEditorView.Unload's remarks on why native
    /// SKBitmap memory needs an explicit dispose rather than waiting on the GC.</summary>
    public void Unload()
    {
        _cutout?.Dispose();
        _cutout = null;
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
    }
}
