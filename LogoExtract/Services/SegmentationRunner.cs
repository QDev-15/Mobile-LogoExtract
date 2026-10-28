using SkiaSharp;

namespace LogoExtract.Services;

/// <summary>Owner's report, 2026-10-28: "không đồng nhất quản lý". This exact
/// Task.Run+BackgroundPriority+timeout-race+late-fault-observer shape used to be copy-pasted, with small
/// unexplained differences, into SegmentationCoordinator.RunOneAsync, EditViewModel.RunAutoSegmentAsync,
/// EditViewModel.ConfirmRegionSelectAsync and ItemsViewModel.RetrySegmentationAsync (EditViewModel absorbed the
/// former SegmentViewModel's two call sites when the segment+edit screens merged, 2026-10-29) -- including one real bug
/// only one of the four copies had (ItemsViewModel disposed its source image immediately on timeout, while a
/// background thread could still be reading it; SegmentationCoordinator's copy knew to wait). One shared
/// implementation means that kind of drift can't happen again, and a future fix (e.g. a different timeout
/// policy) only has to change here.</summary>
public static class SegmentationRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public enum Outcome { Success, TimedOut, Failed }

    public sealed class Result
    {
        public required Outcome Outcome { get; init; }
        public SKBitmap? Mask { get; init; }
        public Exception? Error { get; init; }
    }

    /// <summary>Runs <paramref name="segment"/> on a low-priority background thread with a soft timeout.
    /// <paramref name="logTag"/> identifies the caller in logcat (e.g. "Import", "Load", "Home-Retry").
    /// <paramref name="disposeIfOrphaned"/> is disposed once the call eventually finishes IF it times out -- not
    /// immediately on timeout, since the background thread may still be reading it; pass the source image here
    /// when the caller doesn't otherwise own/keep it alive past this call (the bug fixed by unifying this).</summary>
    public static async Task<Result> RunAsync(Func<SKBitmap> segment, string logTag, TimeSpan? timeout = null, IDisposable? disposeIfOrphaned = null)
    {
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        Task<SKBitmap> task;
        try
        {
            task = Task.Run(() => BackgroundPriority.RunLowPriority(segment));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[{logTag}] segmentation FAILED to start: {ex}");
            return new Result { Outcome = Outcome.Failed, Error = ex };
        }

        if (await Task.WhenAny(task, Task.Delay(effectiveTimeout)) != task)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[{logTag}] segmentation TIMED OUT after {effectiveTimeout.TotalSeconds}s");
            _ = task.ContinueWith(t => ObserveLateFault(t, logTag, disposeIfOrphaned), TaskScheduler.Default);
            return new Result { Outcome = Outcome.TimedOut };
        }

        try
        {
            SKBitmap mask = await task;
            return new Result { Outcome = Outcome.Success, Mask = mask };
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("LogoExtractPerf", $"[{logTag}] segmentation FAILED: {ex}");
            return new Result { Outcome = Outcome.Failed, Error = ex };
        }
    }

    private static void ObserveLateFault(Task<SKBitmap> task, string logTag, IDisposable? extra)
    {
        if (task.Exception != null)
            Android.Util.Log.Error("LogoExtractPerf", $"[{logTag}] late segmentation result after timeout: {task.Exception}");
        else if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
        extra?.Dispose();
    }
}
