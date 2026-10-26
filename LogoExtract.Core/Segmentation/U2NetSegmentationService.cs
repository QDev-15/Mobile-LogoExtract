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

    public U2NetSegmentationService(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    public SegmentationResult Segment(SKBitmap image)
    {
        using SKBitmap resized = image.Resize(new SKImageInfo(InputSize, InputSize), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");

        var input = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                SKColor c = resized.GetPixel(x, y);
                input[0, 0, y, x] = (c.Red / 255f - Mean[0]) / Std[0];
                input[0, 1, y, x] = (c.Green / 255f - Mean[1]) / Std[1];
                input[0, 2, y, x] = (c.Blue / 255f - Mean[2]) / Std[2];
            }
        }

        string inputName = _session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        // u2net has 6 deep-supervision side outputs plus the final fused one; the FIRST output ("d0" in the
        // original model) is the fused, highest-quality one -- confirmed by inspecting session.OutputMetadata
        // order and by eyeballing results against the other 6 on the same sample photos (2026-10-27).
        Tensor<float> maskTensor = outputs.First().AsTensor<float>();

        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
            {
                float v = maskTensor[0, 0, y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        float range = Math.Max(max - min, 1e-6f);

        // Rgba8888, not Gray8: confirmed on-device (emulator, 2026-10-27) that SKCanvas.DrawCircle onto a Gray8
        // SKBitmap is a silent no-op (no exception, no crash, no visible effect) -- MaskEditorView's brush tool
        // needs a canvas-drawable format. Luminance-style semantics kept identical (every channel = R = the
        // mask value, so GetPixel(...).Red still reads correctly everywhere else in this codebase).
        using var small = new SKBitmap(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
            {
                byte v = (byte)Math.Clamp((maskTensor[0, 0, y, x] - min) / range * 255f, 0, 255);
                small.SetPixel(x, y, new SKColor(v, v, v));
            }

        SKBitmap fullSize = small.Resize(new SKImageInfo(image.Width, image.Height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
        return new SegmentationResult(fullSize);
    }

    public void Dispose() => _session.Dispose();
}
