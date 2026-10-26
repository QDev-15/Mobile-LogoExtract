using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using SkiaSharp;

namespace LogoExtract.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    private readonly ItemStore _store;

    public ImportViewModel(ItemStore store)
    {
        _store = store;
    }

    [ObservableProperty]
    private bool isBusy;

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            await Shell.Current.DisplayAlert("Không dùng được máy ảnh", "Thiết bị này không hỗ trợ chụp ảnh trực tiếp.", "Đóng");
            return;
        }
        FileResult? photo = await MediaPicker.Default.CapturePhotoAsync();
        if (photo == null) return;
        IsBusy = true;
        try { await ImportOneAsync(photo); }
        finally { IsBusy = false; }
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task PickAsync()
    {
        IReadOnlyList<FileResult> photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Chọn ảnh áo có logo" });
        if (photos.Count == 0) return;
        IsBusy = true;
        try
        {
            string batchName = photos.Count > 1 ? $"Nhập {DateTime.Now:dd/MM/yyyy HH:mm} ({photos.Count} ảnh)" : $"Nhập {DateTime.Now:dd/MM/yyyy HH:mm}";
            BatchGroup batch = _store.CreateBatch(batchName);
            foreach (FileResult photo in photos)
                await ImportOneAsync(photo, batch.Id);
        }
        finally { IsBusy = false; }
        await Shell.Current.GoToAsync("..");
    }

    private async Task ImportOneAsync(FileResult photo, string? batchId = null)
    {
        batchId ??= _store.CreateBatch($"Nhập {DateTime.Now:dd/MM/yyyy HH:mm}").Id;
        string name = Path.GetFileNameWithoutExtension(photo.FileName);
        LogoItem item = _store.Create(batchId, name.Length > 0 ? name : "Ảnh mới", "original" + Path.GetExtension(photo.FullPath));
        string folder = _store.ItemFolder(item.Id);
        string dest = Path.Combine(folder, item.OriginalFile);
        using (Stream source = await photo.OpenReadAsync())
        using (FileStream destStream = File.Create(dest))
            await source.CopyToAsync(destStream);

        // A lightweight thumbnail right away, not only after segmentation finishes (review 2026-10-27): without
        // this, ItemRow.ThumbPath fell back to the ORIGINAL file for any item still in the "Imported" state --
        // a CollectionView decoding full-resolution camera photos (routinely several MB each) just to show a
        // 64x64 row icon, for every item in a freshly-imported batch. Runs on a background thread; a thumbnail
        // failure must never fail the import itself (the item is still usable, just shows the full photo as its
        // thumbnail until segmentation runs), hence the broad catch.
        try
        {
            await Task.Run(() =>
            {
                using SKBitmap full = ExifOrientation.DecodeOriented(dest);
                int thumbHeight = (int)(320.0 * full.Height / full.Width);
                using SKBitmap? thumb = full.Resize(new SKImageInfo(320, Math.Max(thumbHeight, 1)), SKSamplingOptions.Default);
                if (thumb == null) return;
                using SKData data = thumb.Encode(SKEncodedImageFormat.Png, 90);
                using FileStream stream = File.Create(Path.Combine(folder, "thumb.png"));
                data.SaveTo(stream);
            });
            _store.Update(item.Id, i => i with { ThumbFile = "thumb.png" });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Decode/resize failure on a weird input file: leave ThumbFile null, not fatal to the import.
        }
    }
}
