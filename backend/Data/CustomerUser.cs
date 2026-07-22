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

    // {IsMfaRegistered, IsMfaCapable, Methods: string[]} from the Reports API
    // (reuses Reports.Read.All, no separate permission). Null when never
    // collected/permission missing, same convention as the mailbox fields.
    public string? MfaJson { get; set; }

    // Array of {Name, Enabled, ForwardsTo} — just the inbox rules that
    // auto-forward or redirect mail, the classic BEC/phishing persistence
    // indicator. Derived from InboxRulesJson below at collection time so the
    // Security tab's flagged view doesn't need to re-filter on every read.
    // Needs MailboxSettings.Read. Empty array (not null) means "checked, found none".
    public string? ForwardingRulesJson { get; set; }

    // Array of {Name, Enabled, Sequence, ConditionTypes, ActionTypes,
    // ForwardsTo} — every inbox rule, not just forwarding ones. Same
    // MailboxSettings.Read permission and Graph call as ForwardingRulesJson;
    // that field is just a filtered view of this one.
    public string? InboxRulesJson { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}
