using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Segmentation;
using LogoExtract.Services;
using LogoExtract.Views;
using SkiaSharp;

namespace LogoExtract.ViewModels;

/// <summary>One row of the items-view list (PLAN.md 2.6). Tap/delete are wired from the page's XAML to the
/// parent ItemsViewModel's own commands with this row as the CommandParameter, not owned by the row itself.</summary>
public sealed class ItemRow(LogoItem record, string itemFolder, bool isProcessing)
{
    public LogoItem Record { get; } = record;
    public string Name => Record.Name;
    // isProcessing comes from SegmentationCoordinator, not from Record.State: Imported alone no longer implies
    // "actively running" now that ImportViewModel enqueues background segmentation immediately (2026-10-28) --
    // without this split, a run that failed/timed out and left the item at Imported would keep claiming "Đang
    // tách nền" forever even though nothing is happening, the exact bug this whole change was meant to fix.
    public string StateText => Record.State switch
    {
        ItemState.Imported => isProcessing ? "Đang tách nền" : "Chưa tách nền",
        ItemState.Segmented => "Chờ làm phẳng",
        ItemState.Flattened => "Đã làm phẳng",
        ItemState.Vectorized => "Hoàn tất",
        _ => "",
    };

    // Resolved from Colors.xaml (named resources, not a duplicated hex literal here) -- these 4 are
    // deliberately single, non-AppThemeBinding literals (§2.4: a real pipeline-state distinction, not a brand
    // color), so a plain one-time dictionary lookup is correct; no RequestedThemeChanged subscription needed.
    public Color StateColor => Record.State switch
    {
        ItemState.Imported => ResourceColor("StateImported"),
        ItemState.Segmented => ResourceColor("StateSegmented"),
        ItemState.Flattened => ResourceColor("StateFlattened"),
        ItemState.Vectorized => ResourceColor("StateVectorized"),
        _ => Colors.Gray,
    };

    // The "Hoàn tất" badge uses the success-green literal, which is dark enough to need the same near-black
    // text as a Primary-filled button (see PrimaryDarkText's own remarks); the other 3 badge colors were picked
    // specifically to clear >=4.5:1 under plain White (see Colors.xaml's own remarks on this token group).
    public Color StateTextColor => Record.State == ItemState.Vectorized ? ResourceColor("PrimaryDarkText") : Colors.White;

    public string ThumbPath => Path.Combine(itemFolder, Record.ThumbFile ?? Record.OriginalFile);

    // Owner's request, 2026-10-28: a manual status-override button on the dashboard itself, for exactly the
    // case under investigation that same day (a Release build stuck forever in "Đang tách nền" with no
    // timeout at the time) -- Imported can be force-finished without waiting on the model, and anything past
    // Imported can be sent back to re-run it for real. Flattened/Vectorized route through the same "re-run"
    // path as Segmented; there is no separate manual override for THEIR own later steps, only for segmentation
    // itself, which is the one step this app runs automatically/without the user asking.
    // `&& !isProcessing`: an Imported item with a background run genuinely still queued/in flight is not stuck
    // at all -- offering "Đánh dấu hoàn thành" over something that's actively working would just invite the
    // owner to bail on it needlessly. Only an Imported item with NO run behind it (background one already
    // failed/timed out, or this item predates SegmentationCoordinator) counts as stuck.
    public bool IsStuckState => Record.State == ItemState.Imported && !isProcessing;
    public string ToggleStateLabel => IsStuckState ? "Đánh dấu hoàn thành" : "Tách lại nền";
    public string ToggleStateIcon => IsStuckState ? Icons.Checked : Icons.Refresh;

    private static Color ResourceColor(string key) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color c ? c : Colors.Gray;
}

public partial class ItemsViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private readonly AppDialog _dialog;
    private readonly Lazy<ISegmentationService> _segmentation;
    private readonly SegmentationCoordinator _coordinator;

    // Same GenerationGuard<string> class EditViewModel uses for its own region-select interruption (owner's
    // report, 2026-10-28: "không đồng nhất quản lý... khó theo dõi" -- this used to be a separate hand-rolled
    // Dictionary<string,int> + a pair of local helper methods here, while the segment-screen VM (SegmentViewModel,
    // merged into EditViewModel 2026-10-29) tracked the identical rule with a bare int field; now both screens
    // share one class). Per-item here (unlike EditViewModel, which only ever manages one item) since the dashboard can have a retry in flight for one item while acting
    // on another. Owner's report, 2026-10-28, about the OLD stuck build: "nó cũng đơ luôn không đổi được trạng
    // thái" -- the status button must stay a working escape hatch even while a retry for that SAME item is
    // still running in the background, so re-tapping it bumps the generation and the in-flight call discards
    // its result instead of racing with (or clobbering) whatever the new tap just started.
    private readonly GenerationGuard<string> _generation = new();

    public ItemsViewModel(ItemStore store, Lazy<ISegmentationService> segmentation, AppDialog dialog, SegmentationCoordinator coordinator)
    {
        _store = store;
        _segmentation = segmentation;
        _dialog = dialog;
        _coordinator = coordinator;
        // Background imports (SegmentationCoordinator, 2026-10-28) start/finish on their own schedule, not tied
        // to Home ever appearing -- without this, a batch import would only flip from "Đang tách nền" to "Chờ
        // làm phẳng" the NEXT time the owner happened to navigate back to Home (OnAppearing's own Refresh()),
        // not live while they're actually looking at the list. StateChanged fires on a thread-pool thread (see
        // its own doc comment), hence the dispatch.
        _coordinator.StateChanged += () => MainThread.BeginInvokeOnMainThread(Refresh);
        Refresh();
    }

    public ObservableCollection<ItemRow> Items { get; } = [];

    [ObservableProperty]
    private bool isEmpty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "";

    public void Refresh()
    {
        Items.Clear();
        foreach (LogoItem record in _store.List())
            Items.Add(new ItemRow(record, _store.ItemFolder(record.Id), _coordinator.IsQueuedOrRunning(record.Id)));
        IsEmpty = Items.Count == 0;
    }

    [ObservableProperty]
    private bool isAddExpanded;

    // Drives the new FAB's expand/collapse state (owner's request, 2026-10-29: "nút nhập chuyển thành icon add
    // (dấu +) ... animation trượt lên rồi 2 button hiện lên" -- replaces the old full-page ImportPage navigation
    // entirely, per the owner's explicit "bỏ trang import cũ"). The actual slide-up/fade/icon-rotate animation
    // lives in HomePage.xaml.cs (a View concern -- ViewExtensions.TranslateTo/FadeTo act on a real View), driven
    // off this plain bool via PropertyChanged; see that file's own remarks.
    [RelayCommand]
    private void ToggleAdd() => IsAddExpanded = !IsAddExpanded;

    // Moved here verbatim from the now-retired ImportViewModel/ImportPage (2026-10-29 FAB redesign) -- the FAB's
    // two revealed buttons run these directly instead of navigating to a separate page. Both collapse the FAB
    // tray immediately: the camera/photo-picker UI is about to cover the screen anyway, so there's nothing to
    // gain by leaving the tray open underneath it.
    [RelayCommand]
    private async Task CaptureAsync()
    {
        IsAddExpanded = false;
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            await _dialog.AlertAsync("Không dùng được máy ảnh", "Thiết bị này không hỗ trợ chụp ảnh trực tiếp.", "Đóng");
            return;
        }
        FileResult? photo;
        try
        {
            // Owner's report, 2026-10-29: "không mở được camera để chụp ảnh" -- CapturePhotoAsync had no catch
            // here at all, so ANY failure (CAMERA permission denied/revoked, no default camera app resolving
            // the ACTION_IMAGE_CAPTURE intent -- common on emulators with no Camera app installed, a
            // FileProvider/Activity-result hiccup, ...) threw unobserved and the button just silently did
            // nothing from the owner's own perspective -- no camera UI, no error, no visible reaction at all.
            // Same never-fail-silently shape as every other AppDialog-wrapped call site added this session.
            // Kept intact verbatim across the ImportViewModel -> ItemsViewModel move (2026-10-29).
            photo = await MediaPicker.Default.CapturePhotoAsync();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Home] CapturePhotoAsync FAILED: {ex}");
            // Owner's report, 2026-10-29, as a direct follow-up to the above catch's own first fix: the failure
            // here is specifically a denied CAMERA permission. Once Android's own permission prompt has been
            // denied (especially a second time), the OS stops re-showing it to the app entirely -- there is no
            // in-app API to re-trigger that system dialog, the only way forward is the app's own OS settings
            // screen. "Mở Cài đặt" jumps straight there (AppInfo.Current.ShowSettingsUI(), same pattern
            // DocScanner's PermissionService already uses) instead of just telling the owner to go find it
            // themselves in Android's own Settings app.
            bool openSettings = await _dialog.ConfirmAsync("Không mở được máy ảnh",
                "Chưa có quyền dùng Camera. Mở Cài đặt để cấp quyền, hoặc thử \"Thư viện\" thay thế.",
                "Mở Cài đặt", "Để sau");
            if (openSettings) AppInfo.Current.ShowSettingsUI();
            return;
        }
        if (photo == null) return;
        IsBusy = true;
        BusyText = "Đang nhập ảnh...";
        try { await ImportOneAsync(photo); }
        finally { IsBusy = false; }
        Refresh();
    }

    [RelayCommand]
    private async Task PickAsync()
    {
        IsAddExpanded = false;
        IReadOnlyList<FileResult> photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Chọn ảnh áo có logo" });
        if (photos.Count == 0) return;
        IsBusy = true;
        BusyText = photos.Count > 1 ? $"Đang nhập {photos.Count} ảnh..." : "Đang nhập ảnh...";
        try
        {
            string batchName = photos.Count > 1 ? $"Nhập {DateTime.Now:dd/MM/yyyy HH:mm} ({photos.Count} ảnh)" : $"Nhập {DateTime.Now:dd/MM/yyyy HH:mm}";
            BatchGroup batch = _store.CreateBatch(batchName);
            foreach (FileResult photo in photos)
                await ImportOneAsync(photo, batch.Id);
        }
        finally { IsBusy = false; }
        Refresh();
    }

    // Moved here verbatim from ImportViewModel (2026-10-29) -- creates the LogoItem, copies the file, enqueues
    // background segmentation, and generates a thumbnail. Same behavior/comments as before the move.
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

        // Start segmentation right away, in the background -- NOT awaited, so this import (and a possible
        // multi-photo PickAsync loop around it) never waits on the model. Owner's report, 2026-10-28: before
        // this, nothing ever ran segmentation until the user opened the segment/edit screen for this specific item, so
        // Home's "Đang tách nền" badge on a freshly-imported item was never actually true until then.
        _coordinator.EnqueueIfNeeded(item.Id);

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
                using FileStream stream = File.Create(Path.Combine(folder, CutoutCompositor.ThumbFileName));
                data.SaveTo(stream);
            });
            _store.Update(item.Id, i => i with { ThumbFile = CutoutCompositor.ThumbFileName });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Decode/resize failure on a weird input file: leave ThumbFile null, not fatal to the import.
        }
    }

    // Goes straight to the merged "Chỉnh sửa" screen (owner's request, 2026-10-29: "ở Home khi chọn 1 item sẽ
    // chuyển sang màn hình chỉnh sửa ngay") -- SegmentPage is retired; EditPage now runs/shows segmentation
    // progress itself before falling into color-adjust mode.
    [RelayCommand]
    private async Task OpenAsync(ItemRow row) => await Shell.Current.GoToAsync($"{nameof(Views.EditPage)}?itemId={row.Record.Id}");

    // Was an instant delete on tap -- the one destructive action in this app with no confirmation at all
    // (consistency pass, 2026-10-27). Same shape as every other app's delete-confirm: AppDialog.ConfirmAsync,
    // destructive: true turns the accept button the portfolio-wide #D32F2F, never deletes until accepted.
    [RelayCommand]
    private async Task DeleteAsync(ItemRow row)
    {
        bool ok = await _dialog.ConfirmAsync("Xoá mục này?", $"Xoá \"{row.Name}\"? Không thể hoàn tác.", "Xoá", "Huỷ", destructive: true);
        if (!ok) return;
        _store.Delete(row.Record.Id);
        Refresh();
    }

    /// <summary>Owner's request, 2026-10-28: a status-override button on the dashboard, for exactly the case
    /// under investigation that same day (a Release build stuck forever "Đang tách nền"). From Imported, marks
    /// the item done WITHOUT running the model -- a full-keep mask (whole photo kept, no background actually
    /// removed) the user can brush down by hand later in EditPage's "Chỉnh sửa" sub-toolbar, same as any other mask. From any later
    /// state, sends the item back to Imported and immediately re-runs the real model, same as the owner asked:
    /// "nếu ở trạng thái hoàn thành mà chuyển thành trạng thái tách nền thì sẽ tự động tách nền".</summary>
    [RelayCommand]
    private async Task ToggleStateAsync(ItemRow row)
    {
        if (row.IsStuckState) await MarkCompleteAsync(row.Record);
        else await RetrySegmentationAsync(row.Record);
    }

    private async Task MarkCompleteAsync(LogoItem item)
    {
        bool ok = await _dialog.ConfirmAsync(
            "Đánh dấu hoàn thành?",
            "Bỏ qua tách nền tự động và giữ toàn bộ ảnh (chưa tách nền thật). Bạn có thể tô tay lại sau bằng \"Chọn vùng\".",
            "Đánh dấu", "Huỷ");
        if (!ok) return;

        // Registers with the SAME coordinator the background import queue and Home's own "Tách lại nền" use
        // (see SegmentationCoordinator.TryBeginExternalRun's own remarks) -- without this, opening EditPage
        // for this exact item during the brief window this runs would start a second, concurrent write to the
        // same cutout.png/thumb.png/mask.png files instead of waiting for this one to finish.
        using IDisposable? slot = _coordinator.TryBeginExternalRun(item.Id);
        if (slot == null)
        {
            await _dialog.AlertAsync("Đang xử lý", "Mục này đang được tách nền ở nơi khác, vui lòng đợi rồi thử lại.");
            return;
        }

        int generation = _generation.Bump(item.Id);
        IsBusy = true;
        BusyText = "Đang xử lý...";
        Refresh(); // so the row's badge reflects isProcessing=true right away, same as RetrySegmentationAsync's own Refresh() below
        string folder = _store.ItemFolder(item.Id);
        string path = Path.Combine(folder, item.OriginalFile);
        try
        {
            await Task.Run(() =>
            {
                using SKBitmap image = ExifOrientation.DecodeOriented(path);
                using SKBitmap mask = CutoutCompositor.CreateFullKeepMask(image.Width, image.Height);
                CutoutCompositor.WriteMask(mask, folder);
                CutoutCompositor.WriteCutoutAndThumb(image, mask, folder);
            });
            // A newer tap on this same item (either button) already took over -- don't stomp its result.
            if (_generation.IsCurrent(item.Id, generation)) _store.MarkSegmented(item.Id);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Home] MarkComplete FAILED: {ex}");
            if (_generation.IsCurrent(item.Id, generation)) await _dialog.AlertAsync("Lỗi", "Không thể đánh dấu hoàn thành.");
        }
        finally
        {
            if (_generation.IsCurrent(item.Id, generation)) IsBusy = false;
            Refresh();
        }
    }

    private async Task RetrySegmentationAsync(LogoItem item)
    {
        bool ok = await _dialog.ConfirmAsync(
            "Tách lại nền?",
            "Chạy lại tách nền tự động cho mục này, có thể mất vài chục giây.",
            "Tách lại", "Huỷ");
        if (!ok) return;

        // Same coordinator registration as MarkCompleteAsync's own -- see TryBeginExternalRun's remarks. This is
        // the fix for the owner's own report, 2026-10-28: opening EditPage for this item right after tapping
        // "Tách lại nền" used to start a SECOND, concurrent segmentation run (the segment-screen VM's own
        // WaitIfRunningAsync had nothing to find, since this retry used to run completely outside the coordinator).
        using IDisposable? slot = _coordinator.TryBeginExternalRun(item.Id);
        if (slot == null)
        {
            await _dialog.AlertAsync("Đang xử lý", "Mục này đang được tách nền ở nơi khác, vui lòng đợi rồi thử lại.");
            return;
        }

        int generation = _generation.Bump(item.Id);

        // Flips the badge to "Đang tách nền" and drops the old cutout right away, before the (possibly slow)
        // model call even starts -- so the list reflects the new state immediately instead of only after the
        // model finishes, matching the owner's own framing ("chuyển thành trạng thái tách nền thì sẽ tự động
        // tách nền", i.e. the state change and the auto-run are the same action, not state-change-then-wait).
        _store.ResetToImported(item.Id);
        Refresh();

        IsBusy = true;
        BusyText = "Đang tách nền...";
        string folder = _store.ItemFolder(item.Id);
        string path = Path.Combine(folder, item.OriginalFile);
        SKBitmap image;
        try
        {
            image = await Task.Run(() => ExifOrientation.DecodeOriented(path));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Home] retry decode FAILED: {ex}");
            if (_generation.IsCurrent(item.Id, generation))
            {
                IsBusy = false;
                await _dialog.AlertAsync("Lỗi", "Không thể tách nền. Thử lại hoặc dùng \"Đánh dấu hoàn thành\".");
            }
            Refresh();
            return;
        }

        // disposeIfOrphaned: image -- same reasoning as SegmentationCoordinator's own call (see its remarks):
        // on timeout the background call may still be reading it, so it must not be disposed right away.
        SegmentationRunner.Result result = await SegmentationRunner.RunAsync(
            () => _segmentation.Value.Segment(image).AlphaMask, "Home-Retry", disposeIfOrphaned: image);
        if (result.Outcome == SegmentationRunner.Outcome.TimedOut)
        {
            // "Đánh dấu hoàn thành" (MarkCompleteAsync) is still reachable right now -- it already bumped
            // IsBusy false itself, via Refresh(), the moment the owner tapped it; this check just stops THIS
            // stale call from re-asserting a busy state that tap already moved past.
            if (_generation.IsCurrent(item.Id, generation))
            {
                IsBusy = false;
                await _dialog.AlertAsync("Quá lâu", "Tách nền mất quá nhiều thời gian. Có thể bấm \"Đánh dấu hoàn thành\" để tô tay thay thế.");
            }
            Refresh();
            return;
        }
        try
        {
            if (result.Outcome == SegmentationRunner.Outcome.Failed)
            {
                // Left at Imported on failure (already set above) -- the owner still has "Đánh dấu hoàn thành"
                // as a manual way out instead of being stuck again.
                if (_generation.IsCurrent(item.Id, generation))
                    await _dialog.AlertAsync("Lỗi", "Không thể tách nền. Thử lại hoặc dùng \"Đánh dấu hoàn thành\".");
                return;
            }
            using SKBitmap mask = result.Mask!;
            if (!_generation.IsCurrent(item.Id, generation)) return; // superseded while the model call was running
            await Task.Run(() =>
            {
                CutoutCompositor.WriteMask(mask, folder);
                CutoutCompositor.WriteCutoutAndThumb(image, mask, folder);
            });
            if (_generation.IsCurrent(item.Id, generation)) _store.MarkSegmented(item.Id);
        }
        finally
        {
            image.Dispose();
            if (_generation.IsCurrent(item.Id, generation)) IsBusy = false;
            Refresh();
        }
    }
}
