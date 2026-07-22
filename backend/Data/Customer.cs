namespace Spectra.Api.Data;

public class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }

    // The customer's own Entra ID tenant — required to query their directory
    // via client-credentials (application-only) Graph calls.
    public required string TenantId { get; set; }

    // True once the customer's Entra admin has granted admin consent to
    // Spectra's app registration in their tenant. We don't track this
    // proactively — it's set based on whether a collection attempt actually
    // succeeds, since that's the only reliable signal.
    public bool ConsentGranted { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }

    // Tenant-wide security data (not per-user) — JSON blobs rather than
    // related tables for the same reason as CustomerUser.LicensesJson:
    // nothing here needs to be queried/filtered server-side yet.
    // {CurrentScore, MaxScore, CreatedDateTime} or null if never collected/permission missing.
    public string? SecureScoreJson { get; set; }
    // Array of {DisplayName, State} or null if never collected/permission missing.
    public string? ConditionalAccessPoliciesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedByEmail { get; set; }
}
