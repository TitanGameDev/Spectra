namespace Spectra.Api.Data;

// A user collected from a customer's Entra ID tenant via Graph. Stored rather
// than queried live so every signed-in Spectra user can see it without each
// of them needing their own consent/permissions against the customer's tenant.
public class CustomerUser
{
    public int Id { get; set; }
    public int CustomerId { get; set; }

    // Entra's "id" (object id) in the customer's tenant — stable per user, used
    // to upsert on re-collection rather than duplicating rows.
    public required string GraphUserId { get; set; }

    public string? DisplayName { get; set; }
    public string? Mail { get; set; }
    public required string UserPrincipalName { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? OfficeLocation { get; set; }
    public bool AccountEnabled { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }

    // Mailbox usage — from the Reports API (Reports.Read.All), which is a
    // separate permission from the User.Read.All everything else here uses.
    // Null when that permission hasn't been granted yet, not just "empty" —
    // see LastSyncError on Customer for the distinction shown in Settings.
    public long? MailboxSizeBytes { get; set; }
    public int? MailboxItemCount { get; set; }
    public bool? HasArchiveMailbox { get; set; }

    // JSON array of {SkuId, SkuPartNumber} — denormalized rather than a
    // related table for now; each user typically has only a handful of
    // licenses and nothing here needs to be queried/filtered by SKU yet.
    public string? LicensesJson { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
