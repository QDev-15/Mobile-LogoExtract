using LogoExtract.Core.Segmentation;
using SkiaSharp;

namespace LogoExtract.Core.Tests;

/// <summary>Not a real automated test (no assertion on pixel content -- there is no "correct" mask to assert
/// byte-for-byte): just runs the real ONNX model on the real sample photos and writes the composited PNG next
/// to the Python prototype's own outputs, so the two can be eyeballed side by side to confirm the C# wrapper's
/// preprocessing/postprocessing matches (2026-10-27, validating before wiring this into the Android UI).</summary>
public class SegmentationSmokeTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("1.jpg")]
    [InlineData("2.png")]
    [InlineData("3.png")]
    [InlineData("4.png")]
    public void SegmentSample_WritesCompositedCutout(string fileName)
    {
        string modelPath = Path.Combine(RepoRoot, "Mobile-LogoExtract", "LogoExtract", "Resources", "Raw", "Models", "u2netp.onnx");
        // LogoExtract-prototype/ moved from a sibling of Mobile-LogoExtract/ to inside it (2026-10-27, added to git).
        string samplePath = Path.Combine(RepoRoot, "Mobile-LogoExtract", "LogoExtract-prototype", "samples", fileName);
        string outDir = Path.Combine(RepoRoot, "Mobile-LogoExtract", "LogoExtract-prototype", "outputs");
        Assert.True(File.Exists(modelPath), $"Model not found: {modelPath}");
        Assert.True(File.Exists(samplePath), $"Sample not found: {samplePath}");

        using SKBitmap image = SKBitmap.Decode(samplePath) ?? throw new InvalidOperationException("Decode failed");
        using var service = new U2NetSegmentationService(modelPath);
        SegmentationResult result = service.Segment(image);

        using var composited = new SKBitmap(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                SKColor c = image.GetPixel(x, y);
                byte a = result.AlphaMask.GetPixel(x, y).Red; // Gray8: value stored in all channels
                composited.SetPixel(x, y, new SKColor(c.Red, c.Green, c.Blue, a));
            }
        result.AlphaMask.Dispose();

        string outPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(fileName)}_u2netp_csharp_cutout.png");
        using SKImage skImage = SKImage.FromBitmap(composited);
        using SKData data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.Create(outPath);
        data.SaveTo(fs);
    }
}
