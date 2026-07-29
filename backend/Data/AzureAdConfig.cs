namespace Spectra.Api.Data;

// Single-row table — the Azure AD (Entra ID) app registration values this
// deployment uses, set once via the setup wizard (or setup-azure-ad.sh's
// auto-POST) and editable later from Settings -> Authentication. Lives inside
// whichever database is currently active, same convention as
// DatabaseConnectionConfig. Unlike that table, this one has no chicken-and-egg
// problem with database cutover — but it does have a different one at process
// startup: Microsoft.Identity.Web needs TenantId/BackendClientId before
// builder.Build() runs, before any DbContext exists at all. See
// Auth/AzureAdBootstrapStore.cs for how that's handled.
public class AzureAdConfig
{
    public int Id { get; set; }

    public required string TenantId { get; set; }
    public required string FrontendClientId { get; set; }
    public required string BackendClientId { get; set; }

    // e.g. api://<backend-client-id>/access_as_user
    public required string ApiScope { get; set; }

    // Encrypted via ASP.NET Core Data Protection — never stored or returned in plain text.
    public required string EncryptedBackendClientSecret { get; set; }

    public bool IsConfigured { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedByEmail { get; set; }
}
