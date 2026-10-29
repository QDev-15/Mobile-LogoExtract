using LogoExtract.Core.Segmentation;

namespace LogoExtract.Services;

/// <summary>Decides which <see cref="SegmentationProvider"/> the real segmentation session should actually use.
/// Owner's request, 2026-10-29: "setting dùng 1 trong 2 ... khi cài app, app sẽ đánh giá hệ thống rồi recommend
/// dùng phương án nào rồi chạy theo phương án đó. user có thể chỉnh được trong setting" -- on-device NNAPI vs
/// CPU timing is driver/SoC-dependent (one real device measured NNAPI ~2.4x SLOWER, see
/// <see cref="U2NetSegmentationService"/>'s own comment for the numbers), so neither option is safe to assume
/// universally faster; this benchmarks both ONCE on first real use and caches whichever measured faster, with an
/// explicit owner override (Settings) always taking priority over the auto result.
///
/// Persisted via <see cref="Preferences"/> (no DI registration needed -- same plain-static-API shape as
/// <c>AppInfo.Current</c> elsewhere in this app), not <c>LogoExtract.Core</c>: Core stays platform-agnostic
/// (net9.0, no MAUI reference), so this decision layer lives in the app project instead, same boundary
/// <see cref="U2NetSegmentationService"/>'s own doc comment on <see cref="SegmentationProvider"/> describes.</summary>
public static class SegmentationProviderResolver
{
    private const string KeyOverride = "segmentation_provider_override"; // "Cpu" / "Nnapi" / absent = Auto
    private const string KeyAutoRecommendation = "segmentation_auto_recommendation"; // "Cpu" / "Nnapi"
    private const string KeyAutoCpuMs = "segmentation_auto_cpu_ms";
    private const string KeyAutoNnapiMs = "segmentation_auto_nnapi_ms";

    private static readonly object ModelCopyGate = new();
    private static readonly object BenchmarkGate = new();
    private static Task<SegmentationProvider>? _pendingBenchmark;

    /// <summary>The owner's explicit choice from Settings, or null for "Tự động" (follow the benchmark).</summary>
    public static SegmentationProvider? UserOverride
    {
        get => Preferences.Default.Get(KeyOverride, "") switch
        {
            "Cpu" => SegmentationProvider.Cpu,
            "Nnapi" => SegmentationProvider.Nnapi,
            _ => null,
        };
        set
        {
            if (value == null) Preferences.Default.Remove(KeyOverride);
            else Preferences.Default.Set(KeyOverride, value.Value.ToString());
        }
    }

    /// <summary>The cached auto-benchmark result (recommendation + both raw timings, so Settings can show the
    /// owner WHY, e.g. "CPU: 2300ms, NNAPI: 5600ms") -- null if no benchmark has ever run on this device/install
    /// yet (first real segmentation still pending, or data was cleared).</summary>
    public static (SegmentationProvider Recommendation, long CpuMs, long NnapiMs)? AutoResult
    {
        get
        {
            string rec = Preferences.Default.Get(KeyAutoRecommendation, "");
            if (rec is not ("Cpu" or "Nnapi")) return null;
            return (
                rec == "Cpu" ? SegmentationProvider.Cpu : SegmentationProvider.Nnapi,
                Preferences.Default.Get(KeyAutoCpuMs, -1L),
                Preferences.Default.Get(KeyAutoNnapiMs, -1L));
        }
    }

    /// <summary>Kicks off the one-time auto-benchmark in the background, if (and only if) nothing has decided a
    /// provider yet -- owner's request, 2026-10-29, "chạy benchmark ... chỉ được chạy 1 lần khi cài app": call
    /// this once, early, from <c>App</c>'s own constructor (process start, before any page is even shown)
    /// instead of leaving the benchmark to run lazily inside the owner's first real "tách nền" -- that first
    /// real segmentation would otherwise also pay for this benchmark's own ~1-2s (two synthetic ONNX sessions)
    /// stacked on top of its own real work. Fire-and-forget; <see cref="Resolve"/> (called from MauiProgram's own
    /// <c>Lazy&lt;ISegmentationService&gt;</c> factory, whenever the owner's first real segmentation actually
    /// happens) waits for THIS SAME in-flight task instead of starting a redundant second benchmark if it
    /// hasn't finished yet. No-ops instantly (never even touches the model file) once a cached result or an
    /// owner override already exists, so this is safe to call on every app launch, not just the very first.</summary>
    public static void WarmUpInBackground()
    {
        if (UserOverride != null || AutoResult != null) return;
        lock (BenchmarkGate)
        {
            if (_pendingBenchmark != null) return;
            _pendingBenchmark = Task.Run(() => RunBenchmarkAndCache(EnsureModelCopied()));
        }
    }

    /// <summary>The provider the real segmentation session should build with right now: the owner's override if
    /// set, else the cached benchmark recommendation -- waiting for an in-flight <see cref="WarmUpInBackground"/>
    /// benchmark if the owner's first real segmentation happens to land before that finishes, or running the
    /// benchmark itself from scratch in the (normally unreachable, since <c>App</c>'s constructor always starts
    /// one first) case that no warm-up ever ran. Must be called off the UI thread -- see this class's own
    /// <see cref="WarmUpInBackground"/> for why, same cost class either way.</summary>
    public static SegmentationProvider Resolve(string modelPath)
    {
        SegmentationProvider? userOverride = UserOverride;
        if (userOverride != null) return userOverride.Value;

        (SegmentationProvider Recommendation, long CpuMs, long NnapiMs)? cached = AutoResult;
        if (cached != null) return cached.Value.Recommendation;

        Task<SegmentationProvider>? pending;
        lock (BenchmarkGate) { pending = _pendingBenchmark; }
        // Already guaranteed off the UI thread by this method's own contract, so blocking on the in-flight
        // task here (rather than making Resolve itself async, which would ripple into MauiProgram's own
        // synchronous Lazy<T> factory signature) is safe -- no UI SynchronizationContext to deadlock against.
        if (pending != null) return pending.GetAwaiter().GetResult();

        return RunBenchmarkAndCache(modelPath);
    }

    /// <summary>Raw assets (Resources/Raw) ship inside the APK, not as a plain file on disk -- ONNX Runtime's
    /// InferenceSession needs an actual file path (or a byte[], not used here to keep this one-line simple), so
    /// the model is copied to AppDataDirectory once on first run and reused after that. Moved here from
    /// MauiProgram's own former private copy, 2026-10-29, so Settings' "Đo lại tốc độ" button (SettingsViewModel)
    /// has the same model path <see cref="Resolve"/>/<see cref="RunBenchmarkAndCache"/> use, instead of
    /// duplicating this logic or MauiProgram needing to expose an otherwise-private helper. Lock-guarded:
    /// <see cref="WarmUpInBackground"/> (from App's constructor) and the real segmentation factory
    /// (MauiProgram) can now genuinely race to call this for the first time ever, and the body below is only
    /// safe to interleave, not to run truly concurrently (two threads both seeing "file missing" and copying at
    /// once risks one reading a partially-written file).</summary>
    public static string EnsureModelCopied()
    {
        lock (ModelCopyGate)
        {
            string dest = Path.Combine(FileSystem.AppDataDirectory, "u2netp.onnx");
            if (!File.Exists(dest))
            {
                using Stream source = FileSystem.OpenAppPackageFileAsync("Models/u2netp.onnx").GetAwaiter().GetResult();
                using FileStream destStream = File.Create(dest);
                source.CopyTo(destStream);
            }
            return dest;
        }
    }

    /// <summary>Forces a fresh measurement regardless of any cached result -- backs Settings' "Đo lại tốc độ"
    /// button, for a device whose conditions changed (OS update, different thermal state) or just to confirm
    /// the original result still holds.</summary>
    public static SegmentationProvider RunBenchmarkAndCache(string modelPath)
    {
        // BackgroundPriority.RunLowPriority wrapping, 2026-10-29: found the hard way -- the very first on-device
        // test of WarmUpInBackground (called from App's own constructor, i.e. racing against Ads SDK init,
        // Shell/page construction and everything else cold start does) sat stuck over a minute on just the Cpu
        // benchmark, which measured 2.3s in isolation during the owner's own earlier manual A/B test. Same root
        // cause as the real segmentation path's own documented "whole phone felt stuck" bug
        // (U2NetSegmentationService.BuildSessionOptions' own comment) -- an unthrottled ONNX Runtime call
        // competing for every core during exactly the busiest window of the app's lifecycle. Every OTHER
        // session.Run call site in this app already goes through this same wrapper; this one just hadn't yet.
        long cpuMs = BackgroundPriority.RunLowPriority(() => U2NetSegmentationService.BenchmarkSessionRunMs(modelPath, SegmentationProvider.Cpu));
        long nnapiMs = BackgroundPriority.RunLowPriority(() => U2NetSegmentationService.BenchmarkSessionRunMs(modelPath, SegmentationProvider.Nnapi));
        // NNAPI must win by being genuinely faster, not just tie -- on a device with no real NNAPI driver,
        // AppendExecutionProvider_Nnapi's own try/catch (U2NetSegmentationService.BuildSessionOptions) silently
        // falls back to the identical CPU-only config, so "Nnapi" and "Cpu" measure the same thing there; Cpu
        // should still win that tie since it's the simpler, already-proven path.
        SegmentationProvider recommendation = nnapiMs < cpuMs ? SegmentationProvider.Nnapi : SegmentationProvider.Cpu;

        Preferences.Default.Set(KeyAutoRecommendation, recommendation.ToString());
        Preferences.Default.Set(KeyAutoCpuMs, cpuMs);
        Preferences.Default.Set(KeyAutoNnapiMs, nnapiMs);
        Android.Util.Log.Info("LogoExtractPerf", $"[U2Net] benchmark: Cpu={cpuMs}ms Nnapi={nnapiMs}ms -> recommend {recommendation}");
        return recommendation;
    }
}
