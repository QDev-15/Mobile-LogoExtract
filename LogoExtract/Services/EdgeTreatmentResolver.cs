using LogoExtract.Core.Imaging;

namespace LogoExtract.Services;

/// <summary>Owner's choice (Settings) of which <see cref="EdgeTreatment"/> <c>CutoutCompositor.WriteCutoutAndThumb</c>
/// uses to fix the "viền trắng" (white fringe) edge artifact -- owner's request, 2026-10-29: "có bao nhiêu
/// phương án giải quyết... nếu có 2 cách thì làm thêm setting". No benchmarking here (unlike
/// <see cref="SegmentationProviderResolver"/>'s own Cpu-vs-Nnapi choice) -- both techniques are fast, fixed-cost
/// per-pixel operations with no device-dependent timing to measure; this is a pure quality/look preference, so
/// a plain persisted choice (default Decontaminate, the original fix) is all that's needed.</summary>
public static class EdgeTreatmentResolver
{
    private const string Key = "edge_treatment"; // "Decontaminate" / "Choke"

    public static EdgeTreatment Current
    {
        get => Preferences.Default.Get(Key, "") == "Choke" ? EdgeTreatment.Choke : EdgeTreatment.Decontaminate;
        set => Preferences.Default.Set(Key, value.ToString());
    }
}
