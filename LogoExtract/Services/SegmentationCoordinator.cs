using System.Threading.Channels;
using LogoExtract.Core;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Segmentation;
using SkiaSharp;

namespace LogoExtract.Services;

/// <summary>Runs background segmentation for newly-imported items automatically, one item at a time (a plain
/// serial queue -- importing a multi-photo batch must not spin up N concurrent ONNX sessions at once). Owner's
/// report, 2026-10-28: "ảnh đã import vào danh sách ở home nhưng chưa thực sự chạy tách nền mà nó chỉ hiển thị
/// status đang tách [nền]" -- before this, nothing ever ran segmentation until the user opened SegmentPage for
/// that specific item, so Home's "Đang tách nền" badge on a freshly-imported item was a lie the whole time it
/// sat there unopened. ImportViewModel now calls <see cref="EnqueueIfNeeded"/> right after creating each item;
/// this makes the label true. SegmentViewModel calls <see cref="WaitIfRunningAsync"/> before starting its own
/// run so opening the item early (while its background run is still queued/in flight) awaits THAT result
/// instead of paying for a second, redundant ONNX call on the same photo.</summary>
public sealed class SegmentationCoordinator
{
    // Same bound as SegmentViewModel.SegmentTimeout/ItemsViewModel's own copy, for the same reason: ONNX's
    // InferenceSession.Run has no cancellation hook, so a hung model call can't be aborted, only given up on.
    // Essential here specifically BECAUSE the queue is serial -- one stuck item would otherwise block every
    // photo imported after it, forever, not just itself.
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(30);

    private readonly ItemStore _store;
    private readonly Lazy<ISegmentationService> _segmentation;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _queuedOrRunning = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _completions = [];
    private readonly object _gate = new();

    /// <summary>Fires on the thread-pool, NOT the UI thread -- subscribers (ItemsViewModel) must marshal to the
    /// main thread themselves before touching any UI-bound collection.</summary>
    public event Action? StateChanged;

    public SegmentationCoordinator(ItemStore store, Lazy<ISegmentationService> segmentation)
    {
        _store = store;
        _segmentation = segmentation;
        _ = RunLoopAsync();
    }

    public bool IsQueuedOrRunning(string itemId)
    {
        lock (_gate) return _queuedOrRunning.Contains(itemId);
    }

    /// <summary>Enqueues a freshly-imported item for background segmentation. Never awaited by the caller
    /// (ImportViewModel must return to Home right away regardless of how long the model takes) -- a no-op if
    /// this id is already queued/running, so re-importing or re-triggering never double-enqueues.</summary>
    public void EnqueueIfNeeded(string itemId)
    {
        lock (_gate)
        {
            if (!_queuedOrRunning.Add(itemId)) return;
        }
        StateChanged?.Invoke();
        _queue.Writer.TryWrite(itemId);
    }

    /// <summary>Lets a caller that's about to segment this same item itself (SegmentViewModel, opening the page
    /// early) wait for an already-queued/in-flight background run instead of starting a wasteful second ONNX
    /// call on the same photo. Returns false immediately if nothing is queued/running for this id (the normal
    /// case once the queue has drained, or for an item that predates this feature).</summary>
    public Task<bool> WaitIfRunningAsync(string itemId)
    {
        lock (_gate)
        {
            if (!_queuedOrRunning.Contains(itemId)) return Task.FromResult(false);
            if (!_completions.TryGetValue(itemId, out TaskCompletionSource<bool>? tcs))
                _completions[itemId] = tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs.Task;
        }
    }

    private async Task RunLoopAsync()
    {
        await foreach (string itemId in _queue.Reader.ReadAllAsync())
        {
            bool ranToCompletion = await RunOneAsync(itemId);
            TaskCompletionSource<bool>? tcs;
            lock (_gate)
            {
                _queuedOrRunning.Remove(itemId);
                _completions.Remove(itemId, out tcs);
            }
            tcs?.TrySetResult(ranToCompletion);
            StateChanged?.Invoke();
        }
    }

    /// <summary>True if the item ended up segmented (or didn't need to be -- deleted, or already handled by
    /// someone else, e.g. the owner's own "Đánh dấu hoàn thành" dashboard override winning the race against this
    /// queue entry); false on a real failure/timeout, left at <see cref="ItemState.Imported"/> same as before --
    /// SegmentPage's own auto-run (with full user-facing retry/alert handling) or Home's manual override are
    /// still there as a way forward.</summary>
    private async Task<bool> RunOneAsync(string itemId)
    {
        LogoItem? item = _store.Get(itemId);
        if (item == null || item.State != ItemState.Imported) return true;
        string folder = _store.ItemFolder(itemId);
        string path = Path.Combine(folder, item.OriginalFile);

        SKBitmap image;
        try
        {
            image = await Task.Run(() => ExifOrientation.DecodeOriented(path));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Import] background decode FAILED for {itemId}: {ex}");
            return false;
        }

        Task<SKBitmap> segmentTask;
        try
        {
            segmentTask = Task.Run(() => BackgroundPriority.RunLowPriority(() => _segmentation.Value.Segment(image).AlphaMask));
            if (await Task.WhenAny(segmentTask, Task.Delay(SegmentTimeout)) != segmentTask)
            {
                Android.Util.Log.Error("LogoExtractPerf", $"[Import] background segmentation TIMED OUT after {SegmentTimeout.TotalSeconds}s for {itemId}");
                // image is deliberately NOT disposed here -- segmentTask may still be reading it in the
                // background (no cancellation hook, same as every other segmentation call site in this app).
                // The observer below disposes both once that call actually finishes, however late.
                _ = segmentTask.ContinueWith(t => ObserveLateFault(t, image), TaskScheduler.Default);
                return false;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Import] background segmentation FAILED for {itemId}: {ex}");
            image.Dispose();
            return false;
        }

        try
        {
            using SKBitmap mask = await segmentTask;
            // Re-check: SegmentViewModel may have taken over this exact item (user opened it) and already saved
            // its own result while this queue entry was waiting its turn -- that result is simply stale, not
            // wrong; discard rather than overwrite a newer save.
            if (_store.Get(itemId)?.State != ItemState.Imported) return true;
            await Task.Run(() =>
            {
                CutoutCompositor.WriteMask(mask, folder);
                CutoutCompositor.WriteCutoutAndThumb(image, mask, folder);
            });
            _store.Update(itemId, i => i with { State = ItemState.Segmented, CutoutFile = "cutout.png", ThumbFile = "thumb.png" });
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[Import] background segmentation FAILED for {itemId}: {ex}");
            return false;
        }
        finally
        {
            image.Dispose();
        }
    }

    private static void ObserveLateFault(Task<SKBitmap> task, SKBitmap image)
    {
        if (task.Exception != null)
            Android.Util.Log.Error("LogoExtractPerf", $"[Import] late background segmentation result after timeout: {task.Exception}");
        else if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
        image.Dispose();
    }
}
