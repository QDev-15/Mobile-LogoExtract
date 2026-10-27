namespace LogoExtract.Services;

/// <summary>Owner's report, 2026-10-28: a real device left "Đang tách nền" running for 7+ minutes with the
/// WHOLE UI unresponsive -- not just this app's own busy spinner, but input dispatch itself (confirmed via
/// `adb shell dumpsys gfxinfo`, thousands of "High input latency" events during a run), to the point that even
/// this app's own 30-second soft timeout (SegmentViewModel.SegmentTimeout) never fired, because its
/// `Task.Delay` continuation also has to be scheduled on the SAME Android main-thread Looper the inference call
/// was starving. Capping ONNX Runtime's own thread count (see U2NetSegmentationService's constructor) reduces
/// how much work competes for CPU, but on a loaded/throttled device that alone was not enough -- this additionally
/// lowers the CALLING thread's OS scheduling priority for the duration of the model call, which is the standard
/// Android mechanism (not a .NET-level construct) for making the kernel scheduler favor the main/UI thread over
/// a background compute thread whenever they contend for the same CPU, regardless of how saturated the device
/// is. Must be called from code already running in LogoExtract (net10.0-android) -- Android.OS is not available
/// from LogoExtract.Core's net9.0 target, which is why U2NetSegmentationService.Segment itself can't do this.</summary>
public static class BackgroundPriority
{
    public static T RunLowPriority<T>(Func<T> work)
    {
        int original = Android.OS.Process.GetThreadPriority(Android.OS.Process.MyTid());
        Android.OS.Process.SetThreadPriority((int)Android.OS.ThreadPriority.Background);
        try
        {
            return work();
        }
        finally
        {
            // Thread-pool threads are reused -- never leave a lowered priority stuck on one for unrelated work
            // that runs on it next.
            Android.OS.Process.SetThreadPriority(original);
        }
    }
}
