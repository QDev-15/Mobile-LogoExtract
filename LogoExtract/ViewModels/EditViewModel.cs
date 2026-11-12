using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Segmentation;
using LogoExtract.Core.Vectorize;
using LogoExtract.Core.Warp;
using LogoExtract.Services;
using LogoExtract.Views;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;

namespace LogoExtract.ViewModels;

/// <summary>Merged "tách logo khỏi nền" + "chỉnh sửa" screen (owner's request, 2026-10-29: "gộp 2 màn hình ...
/// để giảm bớt số lần chạm/số màn hình"). Home now navigates straight here; this VM both runs/shows
/// segmentation progress (everything SegmentViewModel used to own) AND does color-adjust/vectorize/export
/// (everything EditViewModel already owned). The old SegmentPage/SegmentViewModel are deleted -- their whole
/// state machine (auto-segment-on-load with a 30s timeout, waiting on an in-flight background run, region-select
/// crop-and-resegment, brush add/remove) is relocated here near-verbatim, just with the "Xong" and
/// autoSaveOnSuccess paths no longer navigating anywhere (there is nowhere left to navigate TO -- this IS
/// EditPage now).
///
/// <para><b>Edit-mode state machine</b>: <see cref="IsEditToolbarOpen"/> and <see cref="IsFlattenOpen"/> are two
/// MUTUALLY EXCLUSIVE switches between this page's three visual modes (<see cref="IsFlattenOpen"/> added
/// 2026-11-12 for the manual mesh-warp flatten tool, PLAN.md 2.3). Both false (default, the common case):
/// color-adjust mode -- AdjustablePreviewView shows the (possibly not-yet-existing) cutout read-only,
/// Brightness/Contrast/Saturation sliders are live, no brushing. <see cref="IsEditToolbarOpen"/> true: brush-edit
/// mode -- MaskEditorView shows the current mask for Thêm/Xoá brushing, a second (popup-style) toolbar with
/// Thêm/Xoá/Chọn vùng/Xong renders ON TOP of the main bottom toolbar (which stays visible underneath, per the
/// owner's own spec: "chồng lên trên ... không thay thế nó"). <see cref="IsSelectingRegion"/> is a sub-state of
/// brush-edit mode (unchanged from SegmentViewModel): swaps the sub-toolbar's own two buttons to Huỷ/Xác nhận
/// vùng while the user brushes a rough region for <see cref="ConfirmRegionSelectAsync"/> to crop-and-resegment
/// inside. <see cref="IsFlattenOpen"/> true: FlattenGridView shows a draggable mesh-warp control-point grid over
/// the item's RAW (pre-flatten) cutout, with its own Đặt lại lưới/Huỷ/Áp dụng sub-toolbar -- see
/// <see cref="OpenFlattenToolAsync"/>'s own remarks on why it always re-decodes CutoutFile rather than reusing
/// whatever <see cref="_cutout"/> currently holds. <see cref="IsPreviewMode"/> is the derived "neither sub-mode
/// is open" bool EditPage.xaml binds AdjustablePreviewView's own visibility to.</para>
///
/// <para><b>Busy-lock rule</b> ("khi hành động tách nền đang chạy thì khóa hết các button - action ... trừ hành
/// động chọn vùng"): every slider and every main/sub toolbar button except "Chỉnh sửa" (opens the sub-toolbar)
/// and "Chọn vùng" (<see cref="StartRegionSelectCommand"/>) binds its IsEnabled to <see cref="IsBusy"/> (inverted)
/// in EditPage.xaml. Those two stay unconditionally enabled -- StartRegionSelect already bypasses IsBusy itself
/// (bumps <see cref="_generation"/> to discard whatever auto-segment is still in flight instead of waiting for
/// it), and "Chỉnh sửa" has to stay reachable or there would be no way to ever reach "Chọn vùng" while
/// busy.</para>
///
/// <para><b>No more explicit color-save step</b>: the green-checkmark "Lưu chỉnh màu" button is gone. Dragging a
/// slider only updates the live preview (same as before); <see cref="PersistColorAdjust"/> commits the current
/// Brightness/Contrast/Saturation to the store, called from VectorizeAsync/SaveToGalleryAsync/ExportAsync/
/// FinishAsync so a color edit is never silently lost just because the user tapped one bottom-toolbar action and
/// never a dedicated "save color" action.</para></summary>
[QueryProperty(nameof(ItemId), "itemId")]
public partial class EditViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private readonly AppDialog _dialog;
    // Lazy<T>, resolved from inside Task.Run, not the constructor -- see MauiProgram's own registration comment
    // and SegmentViewModel's identical former field comment: building the ONNX Runtime session is real I/O+CPU
    // work that must never run on the UI thread during page construction/navigation.
    private readonly Lazy<ISegmentationService> _segmentation;
    private readonly SegmentationCoordinator _coordinator;
    private LogoItem? _item;
    private SKBitmap? _image; // original decoded photo (EXIF-oriented) -- owned jointly with MaskEditorView, see Unload()'s own remarks
    private SKBitmap? _mask;  // current alpha mask -- same joint-ownership note as _image
    private SKBitmap? _cutout; // full-resolution segmented result, loaded once from disk -- only ever re-filtered on demand (Export/SaveToGallery/Vectorize), never while dragging a slider
    // Full-resolution RAW (pre-flatten) cutout, decoded fresh only while the flatten tool is open -- deliberately
    // a SEPARATE field from _cutout (which prefers FlattenedFile once one exists, see ReloadCutoutPreview's own
    // remarks): the flatten tool must always warp from the ORIGINAL segmentation result, never from an
    // already-flattened image, or re-applying the restored grid on top of already-warped pixels would double-warp
    // instead of reproducing the same result. Owned solely by this field -- disposed in
    // CancelFlattenTool/ApplyFlattenAsync/Unload.
    private SKBitmap? _flattenSource;

    // Same GenerationGuard<string> class ItemsViewModel uses for its own dashboard overrides (see that class's
    // own remarks) -- relocated verbatim from SegmentViewModel, which used this to let StartRegionSelect
    // invalidate whatever auto-segment/region-resegment is still running instead of waiting for it.
    private readonly GenerationGuard<string> _generation = new();

    public EditViewModel(ItemStore store, Lazy<ISegmentationService> segmentation, AppDialog dialog, SegmentationCoordinator coordinator)
    {
        _store = store;
        _segmentation = segmentation;
        _dialog = dialog;
        _coordinator = coordinator;
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
    private double saturation; // -1..1

    // White balance (added 2026-11-12): per-channel multiplier from the "Lấy mẫu màu" (pick neutral point)
    // eyedropper tool, fed into AdjustablePreviewView's own bindable properties of the same names for the live
    // preview, and folded into ColorAdjust by PersistColorAdjust the same way Brightness/Contrast/Saturation
    // already are. 1.0 each = no correction, matching ColorAdjust.WhiteBalance's own default -- not a tuple
    // [ObservableProperty] since CommunityToolkit.Mvvm's XAML binding story for a ValueTuple property is awkward
    // next to three plain doubles that bind straight to AdjustablePreviewView's own three bindable properties.
    [ObservableProperty]
    private double whiteBalanceR = 1;

    [ObservableProperty]
    private double whiteBalanceG = 1;

    [ObservableProperty]
    private double whiteBalanceB = 1;

    /// <summary>True while the "Lấy mẫu màu" tool is armed -- the next tap on AdjustablePreviewView samples a
    /// pixel (see PickWhiteBalance) instead of being treated as a potential double-tap-to-reset zoom gesture.
    /// Auto-disarms itself the instant a sample is taken (one tap = one pick), so the user doesn't have to
    /// remember to turn it back off.</summary>
    [ObservableProperty]
    private bool isEyedropperArmed;

    // Not a safety net -- see SegmentViewModel's former identical remark on this same default: the real
    // guarantee is that LoadAsync/RunAutoSegmentAsync/ConfirmRegionSelectAsync/SaveMaskAsync set this explicitly
    // on every exit path, so this default is only ever visible for the instant between construction and
    // LoadAsync's own first (synchronous, pre-await) assignment.
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "Đang tách nền...";

    /// <summary>True while the brush sub-toolbar (Thêm/Xoá/Chọn vùng/Xong, or Huỷ/Xác nhận vùng) is open,
    /// overlaid on top of the main bottom toolbar -- see this class's own remarks on the edit-mode state
    /// machine. MaskEditorView is shown instead of AdjustablePreviewView while this is true.</summary>
    [ObservableProperty]
    private bool isEditToolbarOpen;

    /// <summary>True while the user is brushing a rough region (not the final mask) for
    /// <see cref="ConfirmRegionSelectAsync"/> to re-run segmentation inside -- sub-state of
    /// <see cref="IsEditToolbarOpen"/>, swaps the sub-toolbar's own two buttons. Relocated verbatim from
    /// SegmentViewModel.</summary>
    [ObservableProperty]
    private bool isSelectingRegion;

    /// <summary>True while the flatten sub-toolbar (Đặt lại lưới/Huỷ/Áp dụng) and FlattenGridView are open --
    /// see this class's own remarks on the edit-mode state machine. Mutually exclusive with
    /// <see cref="IsEditToolbarOpen"/> (enforced at the two call sites that open either one, not by a shared
    /// setter -- there are only two of them, OpenEditToolbar and OpenFlattenToolAsync, so a third abstraction
    /// would be more machinery than the two if-checks it would replace).</summary>
    [ObservableProperty]
    private bool isFlattenOpen;

    [ObservableProperty]
    private string? vectorPreviewPath;

    /// <summary>Fired whenever <see cref="_image"/>/<see cref="_mask"/> change (first load, region-select
    /// start/cancel/confirm) so EditPage's code-behind can push them into its MaskEditorView -- same wiring
    /// SegmentPage used to have. Fired regardless of whether the brush sub-toolbar is open right now, so
    /// MaskEditorView is always in sync by the time the user opens it.</summary>
    public event Action<SKBitmap, SKBitmap>? ImagesReady;

    /// <summary>Fired once per <see cref="OpenFlattenToolAsync"/> call, handing EditPage's code-behind the
    /// freshly-decoded raw cutout + the grid to restore (the item's saved <see cref="LogoItem.Warp"/>, or an
    /// identity grid for a never-flattened item) so it can push them into FlattenGridView -- same wiring shape as
    /// <see cref="ImagesReady"/>.</summary>
    public event Action<SKBitmap, WarpGrid>? FlattenReady;

    public MaskEditorView.BrushMode Mode { get; private set; } = MaskEditorView.BrushMode.Add;

    /// <summary>True when neither sub-mode is open -- i.e. plain color-adjust preview, AdjustablePreviewView's
    /// own turf. Derived rather than its own [ObservableProperty]: EditPage.xaml needs a single bool to gate
    /// AdjustablePreviewView's visibility, but CommunityToolkit.Mvvm's source generator has no way to know
    /// IsEditToolbarOpen and IsFlattenOpen are related, so the two partial OnXChanged hooks below raise this
    /// property's change manually.</summary>
    public bool IsPreviewMode => !IsEditToolbarOpen && !IsFlattenOpen;

    partial void OnIsEditToolbarOpenChanged(bool value) => OnPropertyChanged(nameof(IsPreviewMode));
    partial void OnIsFlattenOpenChanged(bool value) => OnPropertyChanged(nameof(IsPreviewMode));

    partial void OnItemIdChanged(string value) => _ = LoadAsync();

    /// <summary>Relocated from SegmentViewModel.LoadAsync, adapted to never navigate (this page no longer hands
    /// off to a separate EditPage -- it IS the edit page) and to also load the color-adjust preview once a
    /// cutout exists, since color-adjust mode is this merged page's own default view.</summary>
    private async Task LoadAsync()
    {
        LogoItem? item = _store.Get(ItemId);
        if (item == null)
        {
            // Same gap SegmentViewModel's own review-found fix covers: "item is gone" (deleted mid-navigation)
            // must still end in a dialog + navigating back, never a silently broken page.
            await _dialog.AlertAsync("Lỗi", "Không tìm thấy mục này.");
            await Shell.Current.GoToAsync("..");
            return;
        }
        _item = item;
        // Sliders initialize from the store exactly once per page visit, independent of segmentation status --
        // a later mask-only edit (SaveMaskAsync) must NOT re-read these from the store and clobber whatever the
        // user has already dragged but not yet committed (see PersistColorAdjust's own remarks).
        Brightness = item.ColorAdjust.Brightness;
        Contrast = item.ColorAdjust.Contrast;
        Saturation = item.ColorAdjust.Saturation;
        (WhiteBalanceR, WhiteBalanceG, WhiteBalanceB) = item.ColorAdjust.WhiteBalance;

        string folder = _store.ItemFolder(item.Id);
        string path = Path.Combine(folder, item.OriginalFile);
        string maskPath = Path.Combine(folder, "mask.png");

        // Check the item's status FIRST, before touching IsBusy at all -- same rule as SegmentViewModel's own
        // (owner's framing: "nếu đang tách nền thì mới hiển thị IsBusy còn không thì chỉ hiển thị ảnh đã tách
        // nền"). An item that already has a cutout has nothing running to wait for.
        bool alreadySegmented = item.CutoutFile != null;
        IsBusy = !alreadySegmented;
        BusyText = "Đang tách nền...";
        try
        {
            // EXIF-oriented decode -- see ExifOrientation.cs. Split out from the segmentation Task.Run so
            // _image is set (and StartRegionSelect becomes usable) as soon as this fast decode finishes, without
            // waiting for segmentation itself.
            SKBitmap image = await Task.Run(() => ExifOrientation.DecodeOriented(path));
            _image = image;

            if (alreadySegmented && await TryLoadSavedMaskAsync(image, maskPath))
            {
                ReloadCutoutPreview();
                return;
            }
            if (alreadySegmented)
            {
                // CutoutFile was set but mask.png turned out missing/unreadable -- needs a fresh run after all.
                IsBusy = true;
            }

            // A background run from import time (SegmentationCoordinator) may already be queued/running for
            // this exact item -- wait for THAT instead of paying for a second, redundant ONNX call.
            if (await _coordinator.WaitIfRunningAsync(item.Id))
            {
                item = _store.Get(ItemId) ?? item;
                _item = item;
                if (item.CutoutFile != null && await TryLoadSavedMaskAsync(image, maskPath))
                {
                    ReloadCutoutPreview();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Load] decode FAILED: {ex}");
            IsBusy = false;
            await _dialog.AlertAsync("Lỗi", "Không thể mở ảnh.");
            return;
        }
        // autoSaveOnSuccess: true -- a fresh auto-segment commits straight away instead of waiting for a
        // separate "Xong" tap (owner's existing rule, unchanged: "Chọn vùng" can still interrupt this before it
        // finishes).
        await RunAutoSegmentAsync(autoSaveOnSuccess: true);
    }

    /// <summary>Tries to load an already-saved mask.png for the freshly-decoded <paramref name="image"/> --
    /// relocated verbatim from SegmentViewModel.</summary>
    private async Task<bool> TryLoadSavedMaskAsync(SKBitmap image, string maskPath)
    {
        SKBitmap? savedMask = await Task.Run(() => BitmapIO.DecodeAsRgba8888(maskPath));
        if (savedMask == null) return false;
        _mask = savedMask;
        IsBusy = false;
        ImagesReady?.Invoke(image, savedMask);
        return true;
    }

    /// <summary>Runs (or re-runs) auto-segmentation on the already-decoded <see cref="_image"/> -- relocated
    /// verbatim from SegmentViewModel, only the autoSaveOnSuccess tail (<see cref="SaveMaskAsync"/>) changed to
    /// not navigate.</summary>
    private async Task RunAutoSegmentAsync(bool autoSaveOnSuccess = false)
    {
        if (_image == null) return;
        SKBitmap image = _image;
        int generation = _generation.Current(ItemId);
        IsBusy = true;
        BusyText = "Đang tách nền...";

        SegmentationRunner.Result result = await SegmentationRunner.RunAsync(() => _segmentation.Value.Segment(image).AlphaMask, "Load");
        if (result.Outcome == SegmentationRunner.Outcome.TimedOut)
        {
            if (_generation.IsCurrent(ItemId, generation))
            {
                IsBusy = false;
                await _dialog.AlertAsync("Quá lâu", "Tách nền mất quá nhiều thời gian. Thử lại hoặc dùng \"Chỉnh sửa\" > \"Chọn vùng\" để tô tay.");
            }
            return;
        }
        if (result.Outcome == SegmentationRunner.Outcome.Failed)
        {
            if (_generation.IsCurrent(ItemId, generation))
            {
                IsBusy = false;
                await _dialog.AlertAsync("Lỗi", "Không thể tách nền. Thử lại hoặc dùng \"Chỉnh sửa\" > \"Chọn vùng\" để tô tay.");
            }
            return;
        }
        SKBitmap mask = result.Mask!;
        if (!_generation.IsCurrent(ItemId, generation))
        {
            mask.Dispose();
            return;
        }
        _mask = mask;
        // Hand the mask to MaskEditorView the moment it exists, even on the autoSaveOnSuccess path -- without
        // this, a fresh item's first auto-segment+autosave never calls Editor.Load() at all (see
        // MaskEditorView's own remarks on the exact on-device bug this caused for SegmentPage/EditPage).
        ImagesReady?.Invoke(image, mask);
        if (autoSaveOnSuccess)
        {
            await SaveMaskAsync(mask);
            return;
        }
        IsBusy = false;
    }

    [RelayCommand]
    private void SetAddMode() => Mode = MaskEditorView.BrushMode.Add;

    [RelayCommand]
    private void SetRemoveMode() => Mode = MaskEditorView.BrushMode.Remove;

    /// <summary>Opens the brush sub-toolbar (Thêm/Xoá/Chọn vùng/Xong), switching the image area to
    /// MaskEditorView. Deliberately unconditional (not gated on !IsBusy) -- this is the one and only way to
    /// reach "Chọn vùng" while busy, which must itself stay reachable per the owner's own busy-lock exception.
    /// MaskEditorView is already in sync with whatever _image/_mask currently are (via ImagesReady, fired on
    /// every load/segment step regardless of whether this toolbar is open) -- nothing else to do here.</summary>
    [RelayCommand]
    private void OpenEditToolbar()
    {
        IsFlattenOpen = false; // mutual exclusivity with the flatten sub-mode -- see this class's own remarks
        IsEditToolbarOpen = true;
    }

    /// <summary>Owner's report (relocated from SegmentViewModel): a salient-object model on a full photo finds
    /// the whole shirt, not the small printed logo on it. Hands the editor a blank mask in Add mode so the user
    /// can roughly brush just the logo; <see cref="ConfirmRegionSelectAsync"/> then crops the ORIGINAL photo to
    /// that brushed area and re-runs segmentation inside only that crop.</summary>
    [RelayCommand]
    private void StartRegionSelect()
    {
        if (_image == null || IsSelectingRegion) return;
        // Interrupts whatever auto-segment/region-resegment is still running in the background -- region-select
        // takes priority over waiting for it (see _generation's own field comment).
        _generation.Bump(ItemId);
        IsBusy = false;
        IsSelectingRegion = true;
        if (_mask != null) _mask = CloneBitmap(_mask);
        var blank = new SKBitmap(_image.Width, _image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas c = new(blank)) c.Clear(new SKColor(0, 0, 0, 255)); // R=0 everywhere = "not selected"
        ImagesReady?.Invoke(_image, blank);
    }

    /// <summary>Discards whatever was brushed in region-select mode and goes back to showing the last real mask
    /// untouched -- relocated verbatim from SegmentViewModel. Stays inside brush-edit mode (does not close
    /// <see cref="IsEditToolbarOpen"/>) -- only "Xong" does that.</summary>
    public void CancelRegionSelect()
    {
        if (_image == null) return;
        IsSelectingRegion = false;
        if (_mask != null) ImagesReady?.Invoke(_image, _mask);
        else _ = RunAutoSegmentAsync();
    }

    /// <summary>Relocated verbatim from SegmentViewModel. Takes ownership of <paramref name="selectionMask"/>
    /// (disposed here).</summary>
    public async Task ConfirmRegionSelectAsync(SKBitmap selectionMask)
    {
        if (_image == null) { selectionMask.Dispose(); return; }
        SKRectI? bounds = NonZeroBounds(selectionMask);
        selectionMask.Dispose();
        if (bounds == null)
        {
            IsSelectingRegion = false;
            if (_mask != null) ImagesReady?.Invoke(_image, _mask);
            else _ = RunAutoSegmentAsync();
            return;
        }

        int generation = _generation.Current(ItemId);
        IsBusy = true;
        BusyText = "Đang tách theo vùng đã chọn...";
        SKBitmap image = _image;
        SKRectI cropRect = PadAndClamp(bounds.Value, image.Width, image.Height, 0.20f);

        SegmentationRunner.Result result = await SegmentationRunner.RunAsync(() =>
        {
            using SKBitmap cropped = CropBitmap(image, cropRect);
            using SKBitmap croppedMask = _segmentation.Value.Segment(cropped).AlphaMask;
            return CompositeIntoFullSize(croppedMask, cropRect, image.Width, image.Height);
        }, "Load");
        if (result.Outcome == SegmentationRunner.Outcome.TimedOut)
        {
            if (_generation.IsCurrent(ItemId, generation))
            {
                IsBusy = false;
                IsSelectingRegion = false;
                await _dialog.AlertAsync("Quá lâu", "Tách theo vùng đã chọn mất quá nhiều thời gian. Thử lại.");
            }
            return;
        }
        if (result.Outcome == SegmentationRunner.Outcome.Failed)
        {
            if (_generation.IsCurrent(ItemId, generation))
            {
                IsBusy = false;
                IsSelectingRegion = false;
                await _dialog.AlertAsync("Lỗi", "Không thể tách theo vùng đã chọn.");
            }
            return;
        }
        SKBitmap newMask = result.Mask!;
        if (!_generation.IsCurrent(ItemId, generation)) { newMask.Dispose(); return; }
        IsBusy = false;
        IsSelectingRegion = false;
        _mask?.Dispose();
        _mask = newMask;
        ImagesReady?.Invoke(_image, _mask);
    }

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
                if (span[row + x * 4] <= 10) continue;
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

    private static SKBitmap CompositeIntoFullSize(SKBitmap croppedMask, SKRectI cropRect, int fullWidth, int fullHeight)
    {
        var full = new SKBitmap(fullWidth, fullHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(full);
        canvas.Clear(new SKColor(0, 0, 0, 255));
        canvas.DrawBitmap(croppedMask, cropRect.Left, cropRect.Top);
        return full;
    }

    /// <summary>Writes the (possibly auto- or hand-edited) mask + cutout + thumb to disk and reloads the
    /// color-adjust preview from the fresh cutout -- shared by both the very first auto-segment
    /// (<see cref="RunAutoSegmentAsync"/>'s autoSaveOnSuccess path, where <see cref="IsEditToolbarOpen"/> was
    /// never true to begin with) and the brush sub-toolbar's own "Xong" (EditPage code-behind's OnDone), which
    /// is why this unconditionally closes the sub-toolbar/region-select sub-state on exit instead of taking a
    /// flag for which caller it was -- a no-op flip for the first case, the actual "Xong" action for the
    /// second.</summary>
    public async Task SaveMaskAsync(SKBitmap editedMask)
    {
        if (_item == null || _image == null) return;
        IsBusy = true;
        BusyText = "Đang lưu...";
        string folder = _store.ItemFolder(_item.Id);
        SKBitmap image = _image;
        EdgeTreatment edgeTreatment = EdgeTreatmentResolver.Current;
        await Task.Run(() =>
        {
            CutoutCompositor.WriteMask(editedMask, folder);
            CutoutCompositor.WriteCutoutAndThumb(image, editedMask, folder, edgeTreatment);
        });

        _store.MarkSegmented(_item.Id);
        _item = _store.Get(_item.Id) ?? _item;
        ReloadCutoutPreview();
        IsEditToolbarOpen = false;
        IsSelectingRegion = false;
        IsBusy = false;
    }

    /// <summary>(Re)loads the full-resolution "working image" + its downscaled on-screen preview from disk --
    /// called on first load and every time the mask changes on disk (a SaveMaskAsync commit) or the flatten tool
    /// commits a new FlattenedFile (ApplyFlattenAsync). Deliberately does NOT touch Brightness/Contrast/
    /// Saturation: those are independent of which pixels the cutout contains, so a mask-only edit must not
    /// reset/discard a color tweak the user has made but not yet committed via a bottom-toolbar action (see
    /// <see cref="PersistColorAdjust"/>'s own remarks).
    ///
    /// <para>Prefers <see cref="LogoItem.FlattenedFile"/> over <see cref="LogoItem.CutoutFile"/> once the former
    /// exists (added 2026-11-12, alongside the flatten tool -- PLAN.md's own pipeline order, Segment -> Flatten
    /// -> Color-adjust -> Vectorize): every caller downstream of segmentation (ComputeFullResAdjustedAsync,
    /// hence SaveToGalleryAsync/ExportAsync; VectorizeAsync's width/height) reads <see cref="_cutout"/>, so fixing
    /// the preference HERE, in the one place that fills that field, makes all of them correct for free without
    /// touching each call site. The flatten tool itself does NOT go through this method -- see
    /// <see cref="OpenFlattenToolAsync"/>'s own remarks on why it always re-decodes the raw CutoutFile
    /// instead.</para></summary>
    private void ReloadCutoutPreview()
    {
        _cutout?.Dispose();
        _cutout = null;
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        string? workingFile = _item?.FlattenedFile ?? _item?.CutoutFile;
        if (workingFile == null) return;
        string path = Path.Combine(_store.ItemFolder(_item!.Id), workingFile);
        _cutout = BitmapIO.DecodeAsRgba8888(path);
        if (_cutout == null) return; // corrupted/missing file; leave the preview blank rather than crash
        // Downscaled, not the full-resolution _cutout directly -- see DownscaleForPreview's own remarks.
        PreviewBitmap = DownscaleForPreview(_cutout, 1400);
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

    /// <summary>Builds a mask bitmap in the exact shape ContourTracer.Trace expects (Rgba8888, mask value in the
    /// R channel) from <paramref name="source"/>'s own ALPHA channel -- see VectorizeAsync's own remarks on why
    /// this is the correct post-flatten mask instead of re-reading mask.png. Span-based, not GetPixel/SetPixel,
    /// same convention as every other full-photo-resolution pixel pass in this app.</summary>
    private static SKBitmap ExtractAlphaAsMask(SKBitmap source)
    {
        var mask = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKPixmap srcPx = source.PeekPixels();
        using SKPixmap dstPx = mask.PeekPixels();
        ReadOnlySpan<byte> src = srcPx.GetPixelSpan();
        Span<byte> dst = dstPx.GetPixelSpan();
        for (int i = 0; i + 3 < src.Length; i += 4)
        {
            byte a = src[i + 3];
            dst[i] = a;
            dst[i + 1] = a;
            dst[i + 2] = a;
            dst[i + 3] = 255;
        }
        return mask;
    }

    /// <summary>Computes the FULL-resolution color-adjusted cutout fresh, from <see cref="_cutout"/> -- called
    /// once by ExportAsync/SaveToGalleryAsync right when the user actually asks to export.</summary>
    private async Task<SKBitmap?> ComputeFullResAdjustedAsync()
    {
        if (_cutout == null) return null;
        SKBitmap cutout = _cutout;
        var adjust = new ColorAdjust
        {
            Brightness = Brightness,
            Contrast = Contrast,
            Saturation = Saturation,
            WhiteBalance = (WhiteBalanceR, WhiteBalanceG, WhiteBalanceB),
        };
        return await Task.Run(() => ColorAdjustFilter.Apply(cutout, adjust));
    }

    /// <summary>Commits the current Brightness/Contrast/Saturation/WhiteBalance to the store -- called from every
    /// bottom-toolbar action (owner's rule, 2026-10-29, replacing the removed green-checkmark "Lưu chỉnh màu"
    /// button: "ảnh được lưu thay đổi của 3 công cụ khi người dùng dùng các action ở toolbar dưới cùng" -- extended
    /// to a 4th tool, white balance, 2026-11-12, same rule applies unchanged).</summary>
    private void PersistColorAdjust()
    {
        if (_item == null) return;
        var adjust = new ColorAdjust
        {
            Brightness = Brightness,
            Contrast = Contrast,
            Saturation = Saturation,
            WhiteBalance = (WhiteBalanceR, WhiteBalanceG, WhiteBalanceB),
        };
        _store.Update(_item.Id, i => i with { ColorAdjust = adjust });
        _item = _store.Get(_item.Id) ?? _item;
    }

    [RelayCommand]
    private void ToggleEyedropper() => IsEyedropperArmed = !IsEyedropperArmed;

    [RelayCommand]
    private void ResetWhiteBalance()
    {
        WhiteBalanceR = 1;
        WhiteBalanceG = 1;
        WhiteBalanceB = 1;
    }

    /// <summary>EditPage code-behind's handler for AdjustablePreviewView.ColorPicked -- <paramref name="normalized"/>
    /// is a 0..1, resolution-independent bitmap-space coordinate (that view only ever shows the downscaled
    /// PreviewBitmap, never <see cref="_cutout"/> directly), mapped here onto the FULL-resolution cutout so the
    /// sampled color -- and therefore the computed white-balance multiplier -- is exact, not approximated from a
    /// downscaled copy. Auto-disarms <see cref="IsEyedropperArmed"/> on a successful pick (one tap = one pick).</summary>
    public void PickWhiteBalance(SKPoint normalized)
    {
        if (_cutout == null) return;
        int x = Math.Clamp((int)Math.Round(normalized.X * (_cutout.Width - 1)), 0, _cutout.Width - 1);
        int y = Math.Clamp((int)Math.Round(normalized.Y * (_cutout.Height - 1)), 0, _cutout.Height - 1);
        SKColor sampled = _cutout.GetPixel(x, y);
        (double r, double g, double b) = ColorAdjustFilter.ComputeWhiteBalance(sampled);
        WhiteBalanceR = r;
        WhiteBalanceG = g;
        WhiteBalanceB = b;
        IsEyedropperArmed = false;
    }

    [RelayCommand]
    private void ResetBrightness() => Brightness = 0;

    [RelayCommand]
    private void ResetContrast() => Contrast = 0;

    [RelayCommand]
    private void ResetSaturation() => Saturation = 0;

    /// <summary>Opens the flatten sub-mode (PLAN.md 2.3's "công cụ làm phẳng thủ công", the biggest gap flagged
    /// in CLAUDE.md's "Chưa làm" list, 2026-10-27). Deliberately re-decodes CutoutFile fresh from disk every time
    /// instead of reusing <see cref="_cutout"/> (which may already be the FlattenedFile, see
    /// ReloadCutoutPreview's own remarks) -- the flatten tool must always warp the RAW segmentation result, never
    /// an already-warped one, or restoring the saved grid and continuing to drag would double-warp instead of
    /// reproducing the previously-seen result. The decode runs behind <see cref="IsBusy"/> (same as every other
    /// disk round-trip in this class) since a 12+MP photo's decode is a real, felt delay, not instant.
    /// AllowConcurrentExecutions = false for the same double-tap reason VectorizeAsync's own comment gives --
    /// two concurrent decodes would leak one of the two resulting bitmaps.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenFlattenToolAsync()
    {
        if (_item?.CutoutFile == null)
        {
            await _dialog.AlertAsync("Chưa có ảnh tách nền", "Cần tách nền xong trước khi làm phẳng.", "Đóng");
            return;
        }
        IsEditToolbarOpen = false; // mutual exclusivity -- see this class's own remarks
        IsSelectingRegion = false;
        IsBusy = true;
        BusyText = "Đang mở công cụ làm phẳng...";
        string path = Path.Combine(_store.ItemFolder(_item.Id), _item.CutoutFile);
        SKBitmap? rawCutout = await Task.Run(() => BitmapIO.DecodeAsRgba8888(path));
        IsBusy = false;
        if (rawCutout == null)
        {
            await _dialog.AlertAsync("Lỗi", "Không thể mở ảnh để làm phẳng.", "Đóng");
            return;
        }
        _flattenSource?.Dispose();
        _flattenSource = rawCutout;
        IsFlattenOpen = true;
        // WarpGrid.Identity() for a never-flattened item (null Warp) -- restores the last saved grid otherwise,
        // so re-opening the tool continues from where the user left off instead of starting over (LogoItem.Warp's
        // own doc comment).
        FlattenReady?.Invoke(_flattenSource, _item.Warp ?? WarpGrid.Identity());
    }

    /// <summary>Discards whatever grid edit was in progress -- EditPage code-behind's "Huỷ" handler. Does NOT
    /// touch the item's already-persisted FlattenedFile/Warp (if any); only abandons the CURRENT, uncommitted
    /// drag session.</summary>
    public void CancelFlattenTool()
    {
        IsFlattenOpen = false;
        _flattenSource?.Dispose();
        _flattenSource = null;
    }

    /// <summary>Commits <paramref name="grid"/> (FlattenGridView's own CurrentGrid, read by EditPage code-behind's
    /// "Áp dụng" handler, same ownership shape as SaveMaskAsync/ConfirmRegionSelectAsync reading
    /// MaskEditorView.CurrentMask -- hence a plain public Task method here too, not a [RelayCommand]: EditPage's
    /// code-behind is the only caller, and it already has its own _actionInFlight double-tap guard, the same
    /// pattern OnDone/OnConfirmRegion use). Runs the SAME MeshWarp.Apply call the live preview already
    /// approximated, but on the full-resolution <see cref="_flattenSource"/>, then persists via
    /// ItemStore.MarkFlattened and reloads the color-adjust preview (which will now prefer the fresh
    /// FlattenedFile, see ReloadCutoutPreview's own remarks).</summary>
    public async Task ApplyFlattenAsync(WarpGrid grid)
    {
        if (_item == null || _flattenSource == null) return;
        IsBusy = true;
        BusyText = "Đang áp dụng làm phẳng...";
        string folder = _store.ItemFolder(_item.Id);
        SKBitmap source = _flattenSource;
        string flattenedPath = Path.Combine(folder, CutoutCompositor.FlattenedFileName);
        await Task.Run(() =>
        {
            using SKBitmap flattened = MeshWarp.Apply(source, grid);
            using SKData data = flattened.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream stream = File.Create(flattenedPath);
            data.SaveTo(stream);
        });

        _store.MarkFlattened(_item.Id, grid);
        _item = _store.Get(_item.Id) ?? _item;
        IsFlattenOpen = false;
        _flattenSource?.Dispose();
        _flattenSource = null;
        ReloadCutoutPreview();
        IsBusy = false;
    }

    // AllowConcurrentExecutions = false (owner's request, 2026-10-29, "tránh nhiều action cùng chạy rồi loạn app
    // gây crash"): a double-tap here would otherwise run two concurrent vectorize passes writing the SAME
    // vector.svg file. This bottom-toolbar button is already IsEnabled-gated on !IsBusy in EditPage.xaml, so
    // this is a second, code-level line of defense against the brief window before that binding visually
    // catches up, not the only protection.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task VectorizeAsync()
    {
        if (_item?.CutoutFile == null || _cutout == null) return;
        PersistColorAdjust(); // Vector hoá doesn't touch color, but committing here too keeps the rule simple: "any bottom-toolbar action commits the current color edit".
        IsBusy = true;
        BusyText = "Đang vector hoá...";
        string folder = _store.ItemFolder(_item.Id);
        string svgPath = Path.Combine(folder, "vector.svg");
        SKBitmap cutout = _cutout; // already FlattenedFile-preferring, see ReloadCutoutPreview's own remarks
        int width = cutout.Width, height = cutout.Height;
        // Once a FlattenedFile exists, mask.png on disk is the UN-warped silhouette -- tracing it directly would
        // produce a vector outline that no longer matches the flattened image's actual (warped) logo boundary.
        // The flattened cutout's own alpha channel IS the correctly-warped mask (MeshWarp.Apply warps all 4
        // RGBA channels together, and CutoutCompositor.WriteCutoutAndThumb originally wrote the mask's R value
        // straight into the cutout's alpha channel), so derive the trace mask from there instead of re-reading
        // mask.png whenever a flatten has been applied.
        bool useFlattenedAlpha = _item.FlattenedFile != null;
        bool ok = await Task.Run(() =>
        {
            using SKBitmap? maskForTrace = useFlattenedAlpha
                ? ExtractAlphaAsMask(cutout)
                : BitmapIO.DecodeAsRgba8888(Path.Combine(folder, CutoutCompositor.MaskFileName));
            if (maskForTrace == null) return false;
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
        _item = _store.Get(_item.Id) ?? _item;
        VectorPreviewPath = svgPath;
        await _dialog.AlertAsync("Đã vector hoá",
            "Đã tạo file đường viền (cut path) SVG. Đây là bản v1: đường viền ngoài 1 màu, chưa phải vector tách theo từng vùng màu.",
            "Đóng");
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveToGalleryAsync()
    {
        PersistColorAdjust();
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

    // AllowConcurrentExecutions = false: a double-tap otherwise launches two overlapping Share.Default sheets.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ExportAsync()
    {
        if (_item == null) return;
        PersistColorAdjust();
        string folder = _store.ItemFolder(_item.Id);
        List<ShareFile> files = [];

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

    /// <summary>Pops the whole navigation stack back to the dashboard in one step -- owner's existing request,
    /// unchanged. Commits the current color edit first (see PersistColorAdjust's own remarks), same as every
    /// other bottom-toolbar action.</summary>
    // AllowConcurrentExecutions = false: double-tapping "Hoàn thành" must not fire a second concurrent
    // Shell.GoToAsync("//HomePage") while the first is still unwinding the stack.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task FinishAsync()
    {
        PersistColorAdjust();
        await Shell.Current.GoToAsync("//HomePage");
    }

    /// <summary>Called from EditPage's OnDisappearing. Disposes _cutout/PreviewBitmap -- the full-resolution
    /// bitmap and its downscaled preview are owned solely by this VM. _image/_mask are deliberately NOT disposed
    /// here: they are the SAME SKBitmap instances MaskEditorView's own Load() holds (handed over by reference,
    /// never copied, same joint-ownership contract SegmentViewModel/MaskEditorView always had) -- Editor.Load's
    /// own replace-on-reload logic is what disposes them, exactly as it did when SegmentPage/EditPage were two
    /// separate page instances. Disposing them again here would double-dispose the same native bitmap Editor is
    /// still holding.</summary>
    public void Unload()
    {
        _cutout?.Dispose();
        _cutout = null;
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        // _flattenSource: owned solely by this VM (never handed to a View by reference the way _image/_mask
        // are, see MaskEditorView's own remarks on that different ownership contract) -- safe and necessary to
        // dispose unconditionally here, unlike _image/_mask just above.
        _flattenSource?.Dispose();
        _flattenSource = null;
    }
}
