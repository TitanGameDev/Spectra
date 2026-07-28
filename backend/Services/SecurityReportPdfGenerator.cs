using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Spectra.Api.Services;

public record SecurityReportData(
    string CustomerName,
    DateTimeOffset GeneratedAt,
    int? SecureScoreCurrent,
    int? SecureScoreMax,
    int MfaRegisteredCount,
    int TotalUserCount,
    int ConditionalAccessEnabledCount,
    int ConditionalAccessTotalCount,
    List<OrcaCheckResultDto> EmailSecurityChecks,
    List<OrcaCheckResultDto> IdentityChecks,
    List<OrcaCheckResultDto> DomainChecks,
    List<OrcaCheckResultDto> ComplianceChecks);

// Renders a customer's current security posture as a downloadable PDF,
// styled to match the app's own dark theme (see ReportTheme/ReportComponents)
// rather than a generic report template — this is meant to look like a
// snapshot of the dashboard the customer's contact would see live, not a
// separate "reporting product."
public static class SecurityReportPdfGenerator
{
    public static byte[] Generate(SecurityReportData data)
    {
        var allChecks = data.EmailSecurityChecks
            .Concat(data.IdentityChecks)
            .Concat(data.DomainChecks)
            .Concat(data.ComplianceChecks)
            .ToList();
        var passCount = allChecks.Count(c => c.Status == "pass");
        var failCount = allChecks.Count(c => c.Status == "fail");
        var failingChecks = allChecks.Where(c => c.Status == "fail").ToList();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.PageColor(ReportTheme.PageBackground);
                page.DefaultTextStyle(x => x.FontColor(ReportTheme.TextPrimary).FontSize(10));

                page.Header().Element(c => ReportComponents.ComposeHeader(c, "Security Report", data.CustomerName, data.GeneratedAt));
                page.Content().PaddingHorizontal(32).Element(c => ComposeContent(c, data, passCount, failCount, failingChecks));
                page.Footer().PaddingHorizontal(32).PaddingVertical(14).Element(ReportComponents.ComposeFooter);
            });
        }).GeneratePdf();
    }

    private static void ComposeContent(IContainer container, SecurityReportData data, int passCount, int failCount, List<OrcaCheckResultDto> failingChecks)
    {
        container.PaddingTop(24).Column(column =>
        {
            column.Spacing(20);

            column.Item().Element(c => ComposeSummary(c, data, passCount, failCount));

            column.Item().Element(c => ComposeChecksSection(c, "Email Security", data.EmailSecurityChecks));
            column.Item().Element(c => ComposeChecksSection(c, "Identity", data.IdentityChecks));
            column.Item().Element(c => ComposeChecksSection(c, "Domains", data.DomainChecks));
            column.Item().Element(c => ComposeChecksSection(c, "Compliance", data.ComplianceChecks));

            if (failingChecks.Count > 0)
            {
                column.Item().Element(c => ComposeRecommendedActions(c, failingChecks));
            }
        });
    }

    private static void ComposeSummary(IContainer container, SecurityReportData data, int passCount, int failCount)
    {
        container.Row(row =>
        {
            row.Spacing(12);

            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Secure Score",
                data.SecureScoreCurrent is not null && data.SecureScoreMax is > 0
                    ? $"{Math.Round(100.0 * data.SecureScoreCurrent.Value / data.SecureScoreMax!.Value)}%"
                    : "—"));

            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "MFA Coverage", $"{data.MfaRegisteredCount}/{data.TotalUserCount}"));

            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Conditional Access", $"{data.ConditionalAccessEnabledCount}/{data.ConditionalAccessTotalCount}"));

            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Checks Passing", $"{passCount} pass / {failCount} fail"));
        });
    }

    private static void ComposeChecksSection(IContainer container, string title, List<OrcaCheckResultDto> checks)
    {
        if (checks.Count == 0)
        {
            return;
        }

        container.Column(column =>
        {
            column.Item().PaddingBottom(8).Text(title).FontSize(13).SemiBold().FontColor(ReportTheme.TextPrimary);

            column.Item().Background(ReportTheme.CardBackground).Border(1).BorderColor(ReportTheme.CardBorder).CornerRadius(10).Padding(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(16);
                    columns.RelativeColumn(4);
                    columns.RelativeColumn(3);
                });

                foreach (var check in checks)
                {
                    var dotColor = check.Status switch
                    {
                        "pass" => ReportTheme.StatusGood,
                        "fail" => ReportTheme.StatusBad,
                        _ => ReportTheme.StatusNeutral,
                    };
                    table.Cell().PaddingVertical(6).PaddingLeft(8).Element(c => ReportComponents.StatusDot(c, dotColor));
                    table.Cell().PaddingVertical(6).Text(check.Title).FontSize(9.5f).FontColor(ReportTheme.TextPrimary);
                    table.Cell().PaddingVertical(6).PaddingRight(8).Text(check.CurrentValue).FontSize(9).FontColor(ReportTheme.TextSecondary);
                }
            });
        });
    }

    private static void ComposeRecommendedActions(IContainer container, List<OrcaCheckResultDto> failingChecks)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(8).Text("Recommended Actions").FontSize(13).SemiBold().FontColor(ReportTheme.TextPrimary);

            column.Item().Background(ReportTheme.CardBackground).Border(1).BorderColor(ReportTheme.CardBorder).CornerRadius(10).Padding(14).Column(inner =>
            {
                inner.Spacing(12);
                foreach (var check in failingChecks)
                {
                    inner.Item().Column(item =>
                    {
                        item.Item().Text(check.Title).FontSize(10).SemiBold().FontColor(ReportTheme.TextPrimary);
                        item.Item().PaddingTop(2).Text(check.Remediation).FontSize(9).FontColor(ReportTheme.TextSecondary);
                    });
                }
            });
        });
    }
}
