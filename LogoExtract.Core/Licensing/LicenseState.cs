namespace LogoExtract.Core.Licensing;

/// <summary>Whether the paid unlock ("Pro") has been bought. Same shape as the sibling PdfReader/Minesweeper
/// apps' own LicenseState: no free-use trial counter here either -- LogoExtract's Pro unlock is just "removes
/// ads" (owner's request, 2026-10-29, "giống như các app khác"), so this stays a single flag. Pure data (no
/// Play Billing, no storage): fully unit-testable.</summary>
public sealed record LicenseState(bool IsPro)
{
    public static readonly LicenseState Initial = new(false);

    /// <summary>Short line for the Settings screen.</summary>
    public string SummaryText => IsPro ? "Đã nâng cấp Pro (không quảng cáo)" : "Đang dùng bản miễn phí (có quảng cáo)";
}
