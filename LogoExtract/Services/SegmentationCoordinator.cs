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

    /// <summary>Owner's report, 2026-10-28: opened SegmentPage for an item right after tapping Home's "Tách lại
    /// nền" for that SAME item -- before this, the dashboard's manual retry ran its own segmentation call
    /// completely outside this coordinator, so (1) the row's badge still read "isProcessing=false" (wrong text,
    /// and ItemRow.IsStuckState stayed true, inviting a second, concurrent tap of "Đánh dấu hoàn
    /// thành" on the very same item) and (2) <see cref="WaitIfRunningAsync"/> had nothing to find, so opening
    /// SegmentPage started a SECOND independent segmentation run concurrently with the first -- two writers
    /// racing on the same cutout.png/thumb.png/mask.png files. ItemsViewModel's manual retry/mark-complete now
    /// wrap their own run in this instead of running unregistered: returns null if something is ALREADY
    /// queued/running for this id (caller should fall back to <see cref="WaitIfRunningAsync"/> instead of
    /// starting a duplicate); otherwise returns a handle whose Dispose releases the slot and wakes anyone
    /// waiting on it, exactly like the background queue's own run does when it finishes.</summary>
    public IDisposable? TryBeginExternalRun(string itemId)
    {
        lock (_gate)
        {
            if (!_queuedOrRunning.Add(itemId)) return null;
        }
        StateChanged?.Invoke();
        return new ExternalRun(this, itemId);
    }

    private sealed class ExternalRun(SegmentationCoordinator owner, string itemId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TaskCompletionSource<bool>? tcs;
            lock (owner._gate)
            {
                owner._queuedOrRunning.Remove(itemId);
                owner._completions.Remove(itemId, out tcs);
            }
            // The exact success/failure of an external run isn't tracked here (ItemsViewModel already shows its
            // own alert/retry affordance on failure) -- a waiter only needs to know "it's done, go re-check the
            // item's own state", same as RunLoopAsync's own tcs.TrySetResult on the background path.
            tcs?.TrySetResult(true);
            owner.StateChanged?.Invoke();
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

        // disposeIfOrphaned: image -- on timeout the background call may still be reading it; SegmentationRunner
        // defers the dispose until that call actually finishes, however late, instead of disposing it out from
        // under a thread that's still using it.
        SegmentationRunner.Result result = await SegmentationRunner.RunAsync(
            () => _segmentation.Value.Segment(image).AlphaMask, "Import", disposeIfOrphaned: image);
        if (result.Outcome == SegmentationRunner.Outcome.TimedOut) return false;
        if (result.Outcome == SegmentationRunner.Outcome.Failed) { image.Dispose(); return false; }

        try
        {
            using SKBitmap mask = result.Mask!;
            // Re-check: SegmentViewModel may have taken over this exact item (user opened it) and already saved
            // its own result while this queue entry was waiting its turn -- that result is simply stale, not
            // wrong; discard rather than overwrite a newer save.
            if (_store.Get(itemId)?.State != ItemState.Imported) return true;
            await Task.Run(() =>
            {
                CutoutCompositor.WriteMask(mask, folder);
                CutoutCompositor.WriteCutoutAndThumb(image, mask, folder);
            });
            _store.MarkSegmented(itemId);
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
}
