using ClosedXML.Excel;

namespace Spectra.Api.Services;

public record MfaExportUserRow(
    string DisplayName,
    string Email,
    string? JobTitle,
    string? Department,
    bool MfaRegistered,
    List<string> MfaMethods);

public record MfaExportCustomerSheet(string CustomerName, List<MfaExportUserRow> Users);

// One workbook, one worksheet per customer, listing each enabled user's MFA
// registration status — the multi-tenant equivalent of UserReportPdfGenerator's
// per-customer PDF, for an admin who wants every customer's MFA posture in a
// single file instead of downloading one report per customer.
public static class MfaExportExcelGenerator
{
    private static readonly char[] InvalidSheetNameChars = ['\\', '/', '?', '*', '[', ']', ':'];

    public static byte[] Generate(List<MfaExportCustomerSheet> customers)
    {
        using var workbook = new XLWorkbook();
        var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var customer in customers)
        {
            var sheet = workbook.Worksheets.Add(SanitizeSheetName(customer.CustomerName, usedSheetNames));

            sheet.Cell(1, 1).Value = "Name";
            sheet.Cell(1, 2).Value = "Email";
            sheet.Cell(1, 3).Value = "Job Title";
            sheet.Cell(1, 4).Value = "Department";
            sheet.Cell(1, 5).Value = "MFA Registered";
            sheet.Cell(1, 6).Value = "MFA Methods";
            sheet.Row(1).Style.Font.Bold = true;

            var row = 2;
            foreach (var user in customer.Users)
            {
                sheet.Cell(row, 1).Value = user.DisplayName;
                sheet.Cell(row, 2).Value = user.Email;
                sheet.Cell(row, 3).Value = user.JobTitle ?? "";
                sheet.Cell(row, 4).Value = user.Department ?? "";
                sheet.Cell(row, 5).Value = user.MfaRegistered ? "Yes" : "No";
                sheet.Cell(row, 6).Value = string.Join(", ", user.MfaMethods);
                row++;
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents();
        }

        // A workbook needs at least one worksheet to open at all.
        if (customers.Count == 0)
        {
            workbook.Worksheets.Add("No customers");
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // Excel sheet names: max 31 chars, can't contain \ / ? * [ ] :, and must
    // be unique within the workbook — customer names can collide once
    // truncated (or already collide outright), so dedupe with a numeric
    // suffix rather than let ClosedXML throw on a duplicate name.
    private static string SanitizeSheetName(string name, HashSet<string> usedNames)
    {
        var cleaned = new string(name.Select(c => InvalidSheetNameChars.Contains(c) ? '-' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Customer";
        }
        if (cleaned.Length > 31)
        {
            cleaned = cleaned[..31];
        }

        var candidate = cleaned;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            var suffixText = $" ({suffix})";
            var baseLength = Math.Min(cleaned.Length, 31 - suffixText.Length);
            candidate = cleaned[..baseLength] + suffixText;
            suffix++;
        }
        return candidate;
    }
}
