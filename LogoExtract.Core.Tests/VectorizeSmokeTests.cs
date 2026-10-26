using LogoExtract.Core.Segmentation;
using LogoExtract.Core.Vectorize;
using SkiaSharp;

namespace LogoExtract.Core.Tests;

public class VectorizeSmokeTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Trace_Sample3_WritesSvgNextToPythonOutputs()
    {
        string modelPath = Path.Combine(RepoRoot, "Mobile-LogoExtract", "LogoExtract", "Resources", "Raw", "Models", "u2netp.onnx");
        string samplePath = Path.Combine(RepoRoot, "LogoExtract-prototype", "samples", "3.png");
        string outPath = Path.Combine(RepoRoot, "LogoExtract-prototype", "outputs", "3_vectorize_test.svg");

        using SKBitmap image = SKBitmap.Decode(samplePath)!;
        using var service = new U2NetSegmentationService(modelPath);
        SegmentationResult result = service.Segment(image);

        IReadOnlyList<Contour> contours = ContourTracer.Trace(result.AlphaMask);
        Assert.NotEmpty(contours);

        string svg = ContourTracer.ToSvg(contours, image.Width, image.Height, "#27E3A3");
        File.WriteAllText(outPath, svg);
        result.AlphaMask.Dispose();
    }
}
