using System.Diagnostics;
using LogoExtract.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace LogoExtract.Core.Segmentation;

/// <summary>u2netp (Apache-2.0, from the U-2-Net project; .onnx redistributed by the rembg project, checksum
/// verified against the official release 2026-10-27) run via ONNX Runtime. Fixed 320x320 input (the
/// checkpoint's own training resolution); mask is upscaled back to the original image size with bilinear
/// interpolation, which is enough for a first-pass suggestion the user then brushes/lassos over (not meant to
/// be pixel-perfect on its own -- see <see cref="ISegmentationService"/>'s own remarks).</summary>
public sealed class U2NetSegmentationService : ISegmentationService, IDisposable
{
    private const int InputSize = 320;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;

    public U2NetSegmentationService(string modelPath, SegmentationProvider provider)
    {
        SessionOptions options = BuildSessionOptions(provider);
        var sw = Stopwatch.StartNew();
        _session = new InferenceSession(modelPath, options);
        Console.WriteLine($"[U2Net] InferenceSession ctor ({provider}): {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Builds a session with the given provider, runs ONE inference on a synthetic (zero-filled, no
    /// real photo needed) input, times just <c>session.Run</c>, and disposes the session -- used by
    /// <c>LogoExtract.Services.SegmentationProviderResolver</c> to benchmark Cpu vs Nnapi once on first real use
    /// and cache whichever measured faster (owner's request, 2026-10-29: "khi cài app, app sẽ đánh giá hệ thống
    /// rồi recommend dùng phương án nào"). Timing depends on the model GRAPH's own partitioning/execution, not
    /// the pixel VALUES, so a synthetic tensor gives a representative number without needing a decoded photo.</summary>
    public static long BenchmarkSessionRunMs(string modelPath, SegmentationProvider provider)
    {
        SessionOptions options = BuildSessionOptions(provider);
        using var session = new InferenceSession(modelPath, options);
        var input = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        string inputName = session.InputMetadata.Keys.First();
        var sw = Stopwatch.StartNew();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"[U2Net] benchmark session.Run ({provider}): {ms}ms");
        return ms;
    }

    private static SessionOptions BuildSessionOptions(SegmentationProvider provider)
    {
        // Owner's report, 2026-10-28, on a real device (Samsung Note10+, 8 cores): the WHOLE PHONE felt stuck
        // during segmentation, not just this app -- `adb shell dumpsys gfxinfo` during a run showed "Number
        // High input latency: 8606", confirming input dispatch itself was starved, not just this app's own UI
        // thread being slow. Root cause: InferenceSession with default SessionOptions lets ONNX Runtime's
        // intra-op thread pool spin up to one thread per CPU core, which (combined with the GC pressure from
        // this call's own per-pixel loops) starves the ENTIRE system of scheduling headroom, including the
        // input pipeline, even though the .NET Task.Run itself is correctly off the managed UI thread. Capping
        // both pools leaves real headroom for the rest of the system; measured on-device, this did not make a
        // single inference noticeably slower (ORT's gain from more threads on a model this small was already
        // marginal) while input latency returned to normal.
        // 1/1, not 2/1 (first tried 2026-10-28): on a loaded/throttled real device, 2 intra-op threads still
        // left the whole system's input dispatch badly starved (thousands of "High input latency" events in
        // `dumpsys gfxinfo` during one run) -- see BackgroundPriority's own remarks for the other half of this
        // fix (OS thread priority), which matters more than this number once the call is already sequential.
        // Applies to BOTH providers below, Cpu and Nnapi -- NNAPI still runs whatever fraction of the graph it
        // can't/won't accelerate through this same capped CPU EP (see the per-partition fallback note below).
        var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        Console.WriteLine($"[U2Net] SessionOptions: IntraOpNumThreads={options.IntraOpNumThreads}, InterOpNumThreads={options.InterOpNumThreads}");

        if (provider == SegmentationProvider.Nnapi)
        {
            // Owner's request, 2026-10-29, "tối ưu hoá tốc độ ... thử NNAPI/GPU trước": NNAPI is Android's own
            // on-device NN accelerator API -- the closest thing to "GPU" ONNX Runtime's standard Android build
            // exposes here, there is no separate GPU EP in it (see the XA0141 build warning on
            // libonnxruntime4j_jni.so, already known from this project's own build log). Appended as an
            // ADDITIONAL execution provider, not a replacement for the CPU one above: ORT partitions the graph
            // per-op, so any op NNAPI's driver can't/won't accelerate on this device falls back to the CPU EP
            // automatically, same session, same Segment() call site.
            // NNAPI_FLAG_USE_FP16: this model's own doc comment already notes its mask is a first-pass
            // suggestion upscaled with bilinear interpolation, not meant to be pixel-perfect -- that existing
            // tolerance for imprecision is exactly what FP16 relaxation trades for speed, and it also unlocks
            // hardware backends that reject full FP32 graphs on some devices.
            // MEASURED, 2026-10-29: on-device A/B (`adb logcat`, same device, same two photos, right after each
            // other) found NNAPI ~2.4x SLOWER than Cpu here -- its own `GetCapability` log showed the graph
            // split into 67 separate partitions (300-442 of 371-513 nodes went to NNAPI, the rest stayed CPU),
            // and `session.Run` measured 5524-5658ms with NNAPI vs. 2247-2300ms Cpu-only on the SAME
            // device/photos. That is exactly WHY this is no longer hardcoded one way: NNAPI driver quality
            // varies enough by SoC vendor that it can plausibly win on different hardware, hence benchmarking
            // per-device (SegmentationProviderResolver) instead of assuming either is universally faster.
            try
            {
                options.AppendExecutionProvider_Nnapi(NnapiFlags.NNAPI_FLAG_USE_FP16);
            }
            catch (Exception ex)
            {
                // NNAPI only exists from Android 8.1 (API 27) -- this app's own minSdk is 26 -- and some real
                // devices have no NNAPI driver at all; AppendExecutionProvider_Nnapi throws on a genuinely
                // unsupported platform rather than silently no-op'ing, so CPU-only (already configured above)
                // is this catch's fallback, not a hard failure.
                Console.WriteLine($"[U2Net] NNAPI unavailable, staying CPU-only: {ex.Message}");
            }
        }
        return options;
    }

    public SegmentationResult Segment(SKBitmap image)
    {
        var sw = Stopwatch.StartNew();
        // Rgba8888 explicit, not the platform-default color type Resize would otherwise pick (same reasoning as
        // CutoutCompositor/GuidedFilter's own explicit color types): the raw-span pixel access below assumes a
        // known R,G,B,A byte order, which platform-default N32 does NOT guarantee (Bgra8888 on some platforms).
        using SKBitmap resized = image.Resize(new SKImageInfo(InputSize, InputSize, SKColorType.Rgba8888), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
        Console.WriteLine($"[U2Net] resize: {sw.ElapsedMilliseconds}ms (source {image.Width}x{image.Height})");
        sw.Restart();

        // Owner's report, 2026-10-28: minutes-long real-device runs vs. ~1.5-2s for the same model/image on a
        // desktop PC -- far more than mobile-vs-desktop CPU speed alone explains. DenseTensor<T>'s own
        // multi-dim indexer (`tensor[0,0,y,x]`) and SKBitmap.GetPixel/SetPixel are both documented-slow-path
        // APIs (generic indexing / per-call native interop) that a desktop JIT can hide the cost of but an
        // Android Release AOT build generally cannot anywhere near as well; with ~300K+ calls each in these
        // loops (320x320x3 and 320x320x2), even a modest per-call AOT penalty compounds into real seconds-to-
        // minutes. Span-based access (DenseTensor.Buffer, SKPixmap.GetPixelSpan -- the same pattern already
        // used by CutoutCompositor/GuidedFilter elsewhere in this file's own namespace) bypasses both.
        var input = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        Span<float> inputSpan = input.Buffer.Span;
        int plane = InputSize * InputSize;
        using (SKPixmap resizedPx = resized.PeekPixels())
        {
            ReadOnlySpan<byte> rgba = resizedPx.GetPixelSpan();
            for (int i = 0; i < plane; i++)
            {
                int o = i * 4;
                inputSpan[i] = (rgba[o] / 255f - Mean[0]) / Std[0];
                inputSpan[plane + i] = (rgba[o + 1] / 255f - Mean[1]) / Std[1];
                inputSpan[2 * plane + i] = (rgba[o + 2] / 255f - Mean[2]) / Std[2];
            }
        }
        Console.WriteLine($"[U2Net] build input tensor: {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        string inputName = _session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        Console.WriteLine($"[U2Net] session.Run: {sw.ElapsedMilliseconds}ms");
        sw.Restart();
        // u2net has 6 deep-supervision side outputs plus the final fused one; the FIRST output ("d0" in the
        // original model) is the fused, highest-quality one -- confirmed by inspecting session.OutputMetadata
        // order and by eyeballing results against the other 6 on the same sample photos (2026-10-27).
        // ORT's managed outputs are always backed by DenseTensor<T> in practice -- this cast is what unlocks
        // Buffer.Span below instead of the slow generic indexer (see this method's own remarks above).
        var denseMask = (DenseTensor<float>)outputs.First().AsTensor<float>();
        ReadOnlySpan<float> maskSpan = denseMask.Buffer.Span; // length InputSize*InputSize (mask has 1 channel)

        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < maskSpan.Length; i++)
        {
            float v = maskSpan[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        float range = Math.Max(max - min, 1e-6f);
        Console.WriteLine($"[U2Net] min/max scan: {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        // Rgba8888, not Gray8: confirmed on-device (emulator, 2026-10-27) that SKCanvas.DrawCircle onto a Gray8
        // SKBitmap is a silent no-op (no exception, no crash, no visible effect) -- MaskEditorView's brush tool
        // needs a canvas-drawable format. Luminance-style semantics kept identical (every channel = R = the
        // mask value, so GetPixel(...).Red still reads correctly everywhere else in this codebase).
        using var small = new SKBitmap(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKPixmap smallPx = small.PeekPixels())
        {
            Span<byte> dst = smallPx.GetPixelSpan();
            for (int i = 0; i < maskSpan.Length; i++)
            {
                byte v = (byte)Math.Clamp((maskSpan[i] - min) / range * 255f, 0, 255);
                int o = i * 4;
                dst[o] = v; dst[o + 1] = v; dst[o + 2] = v; dst[o + 3] = 255;
            }
        }

        Console.WriteLine($"[U2Net] build mask bitmap: {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        using SKBitmap fullSize = small.Resize(new SKImageInfo(image.Width, image.Height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
        Console.WriteLine($"[U2Net] upscale mask to full size: {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        // Edge-aware refinement against the ORIGINAL full-resolution photo (owner's report, 2026-10-27: "tách
        // vùng tóc chưa được rõ nét, không tách được từng sợi tóc") -- the 320px model pass above already lost
        // any hair-strand-scale detail, so this cannot invent it back, but it does pull the mask's blurry edge
        // onto the photo's real light/dark boundaries instead of a straight bilinear-upscaled curve.
        SKBitmap refined = GuidedFilter.Refine(image, fullSize);
        Console.WriteLine($"[U2Net] guided filter refine: {sw.ElapsedMilliseconds}ms");
        return new SegmentationResult(refined);
    }

    public void Dispose() => _session.Dispose();
}
