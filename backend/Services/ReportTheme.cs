using QuestPDF.Infrastructure;

namespace Spectra.Api.Services;

// Shared PDF report styling — mirrors the app's own dark-mode CSS custom
// properties (frontend/src/index.css) so a generated report looks like a
// snapshot of the dashboard rather than a separate "reporting product."
// Used by every report generator (SecurityReportPdfGenerator,
// UserReportPdfGenerator, ...).
public static class ReportTheme
{
    public const string PageBackground = "#0F0E1A";
    public const string CardBackground = "#1B1930";
    public const string CardBorder = "#2A2840";
    public const string TextPrimary = "#F2F1FA";
    public const string TextSecondary = "#B7B4CF";
    public const string TextTertiary = "#8785A3";
    public const string StatusGood = "#3DDC84";
    public const string StatusBad = "#F27093";
    public const string StatusNeutral = "#8785A3";

    // Same color stops as .brand-mark's conic-gradient in index.css, applied
    // as a linear gradient here — PDF rendering doesn't lend itself to
    // replicating a conic gradient, so this is the closest practical
    // equivalent for the small logo mark.
    public static readonly Color[] BrandGradientStops =
    [
        Color.FromHex("#FF5F6D"),
        Color.FromHex("#FFC371"),
        Color.FromHex("#47E0A1"),
        Color.FromHex("#3EA8FF"),
        Color.FromHex("#7C6CF0"),
    ];
}
