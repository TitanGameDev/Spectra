using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Spectra.Api.Services;

// Disabled accounts are filtered out before this row is ever built (see the
// /users-report endpoint) — a departed user's stale MFA/license/mailbox
// state isn't part of "the people at this customer" for a client-facing
// report, so there's no AccountEnabled field here to render.
public record UserReportUserRow(
    string DisplayName,
    string Email,
    string? JobTitle,
    string? Department,
    bool MfaRegistered,
    List<string> LicenseNames,
    long? MailboxSizeBytes,
    int? MailboxItemCount);

public record UserReportData(
    string CustomerName,
    DateTimeOffset GeneratedAt,
    // False when no mailbox data was ever collected, or every row came back
    // concealed (see Customer.MailboxDataConcealed) — the Mailboxes section
    // is omitted entirely rather than shown full of dashes either way.
    bool MailboxDataAvailable,
    // How many disabled accounts were excluded from Users below — shown as
    // its own summary tile so the total doesn't look mysteriously smaller
    // than the customer's actual headcount.
    int DisabledExcludedCount,
    List<UserReportUserRow> Users);

// Renders a customer's user roster as a downloadable PDF — Directory,
// Licenses, and (when available) Mailbox usage — styled to match the app's
// own dark theme via ReportTheme/ReportComponents, the same as
// SecurityReportPdfGenerator.
public static class UserReportPdfGenerator
{
    public static byte[] Generate(UserReportData data)
    {
        var licensedCount = data.Users.Count(u => u.LicenseNames.Count > 0);
        var mfaRegisteredCount = data.Users.Count(u => u.MfaRegistered);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.PageColor(ReportTheme.PageBackground);
                page.DefaultTextStyle(x => x.FontColor(ReportTheme.TextPrimary).FontSize(10));

                page.Header().Element(c => ReportComponents.ComposeHeader(c, "User Report", data.CustomerName, data.GeneratedAt));
                page.Content().PaddingHorizontal(32).Element(c => ComposeContent(c, data, licensedCount, mfaRegisteredCount));
                page.Footer().PaddingHorizontal(32).PaddingVertical(14).Element(ReportComponents.ComposeFooter);
            });
        }).GeneratePdf();
    }

    private static void ComposeContent(IContainer container, UserReportData data, int licensedCount, int mfaRegisteredCount)
    {
        container.PaddingTop(24).Column(column =>
        {
            column.Spacing(20);

            column.Item().Element(c => ComposeSummary(c, data, licensedCount, mfaRegisteredCount));
            column.Item().Element(c => ComposeDirectory(c, data.Users));
            column.Item().Element(c => ComposeLicenses(c, data.Users));

            if (data.MailboxDataAvailable)
            {
                column.Item().Element(c => ComposeMailboxes(c, data.Users));
            }
        });
    }

    private static void ComposeSummary(IContainer container, UserReportData data, int licensedCount, int mfaRegisteredCount)
    {
        container.Row(row =>
        {
            row.Spacing(12);

            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Active Users", data.Users.Count.ToString()));
            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Licensed", $"{licensedCount}/{data.Users.Count}"));
            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "MFA Registered", $"{mfaRegisteredCount}/{data.Users.Count}"));
            row.RelativeItem().Element(c => ReportComponents.SummaryTile(c, "Disabled (excluded)", data.DisabledExcludedCount.ToString()));
        });
    }

    private static void ComposeDirectory(IContainer container, List<UserReportUserRow> users)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(8).Text("Directory").FontSize(13).SemiBold().FontColor(ReportTheme.TextPrimary);

            column.Item().Background(ReportTheme.CardBackground).Border(1).BorderColor(ReportTheme.CardBorder).CornerRadius(10).Padding(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1.5f);
                });

                table.Header(header =>
                {
                    header.Cell().PaddingLeft(8).Text("Name").FontSize(8).FontColor(ReportTheme.TextTertiary);
                    header.Cell().Text("Email").FontSize(8).FontColor(ReportTheme.TextTertiary);
                    header.Cell().Text("Job Title").FontSize(8).FontColor(ReportTheme.TextTertiary);
                    header.Cell().Text("Department").FontSize(8).FontColor(ReportTheme.TextTertiary);
                    header.Cell().PaddingRight(8).Text("MFA").FontSize(8).FontColor(ReportTheme.TextTertiary);
                });

                foreach (var user in users)
                {
                    table.Cell().PaddingVertical(6).PaddingLeft(8).Text(user.DisplayName).FontSize(9.5f).FontColor(ReportTheme.TextPrimary);
                    table.Cell().PaddingVertical(6).Text(user.Email).FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).Text(user.JobTitle ?? "—").FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).Text(user.Department ?? "—").FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).PaddingRight(8).Text(user.MfaRegistered ? "Yes" : "No")
                        .FontSize(9).FontColor(user.MfaRegistered ? ReportTheme.StatusGood : ReportTheme.StatusBad);
                }
            });
        });
    }

    private static void ComposeLicenses(IContainer container, List<UserReportUserRow> users)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(8).Text("Licenses").FontSize(13).SemiBold().FontColor(ReportTheme.TextPrimary);

            column.Item().Background(ReportTheme.CardBackground).Border(1).BorderColor(ReportTheme.CardBorder).CornerRadius(10).Padding(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(5);
                });

                foreach (var user in users)
                {
                    var hasLicenses = user.LicenseNames.Count > 0;
                    table.Cell().PaddingVertical(6).PaddingLeft(8).Text(user.DisplayName).FontSize(9.5f).FontColor(ReportTheme.TextPrimary);
                    table.Cell().PaddingVertical(6).Text(user.Email).FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).PaddingRight(8).Text(hasLicenses ? string.Join(", ", user.LicenseNames) : "Unlicensed")
                        .FontSize(9).FontColor(hasLicenses ? ReportTheme.TextSecondary : ReportTheme.StatusBad);
                }
            });
        });
    }

    private static void ComposeMailboxes(IContainer container, List<UserReportUserRow> users)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(8).Text("Mailboxes").FontSize(13).SemiBold().FontColor(ReportTheme.TextPrimary);

            column.Item().Background(ReportTheme.CardBackground).Border(1).BorderColor(ReportTheme.CardBorder).CornerRadius(10).Padding(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                });

                foreach (var user in users)
                {
                    table.Cell().PaddingVertical(6).PaddingLeft(8).Text(user.DisplayName).FontSize(9.5f).FontColor(ReportTheme.TextPrimary);
                    table.Cell().PaddingVertical(6).Text(user.Email).FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).Text(FormatBytes(user.MailboxSizeBytes)).FontSize(9).FontColor(ReportTheme.TextSecondary);
                    table.Cell().PaddingVertical(6).PaddingRight(8).Text(user.MailboxItemCount?.ToString() ?? "—").FontSize(9).FontColor(ReportTheme.TextSecondary);
                }
            });
        });
    }

    // Mirrors formatBytes in frontend/src/components/Users.tsx for
    // consistency between the dashboard and this PDF.
    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }
        if (bytes == 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var exponent = Math.Min((int)Math.Floor(Math.Log(bytes.Value) / Math.Log(1024)), units.Length - 1);
        var value = bytes.Value / Math.Pow(1024, exponent);
        return $"{value.ToString(exponent == 0 ? "F0" : "F1")} {units[exponent]}";
    }
}
