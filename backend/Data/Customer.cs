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

    // True when the tenant's mailbox usage report data came back from Graph
    // but every row was keyed by an anonymized identifier that matched no
    // real user — Microsoft conceals real UPNs in usage-report data by
    // default tenant-wide (Microsoft 365 admin center → Settings → Org
    // Settings → Services → Reports → "Display Concealed user, group, and
    // site names in all reports"), which is a completely different problem
    // from Reports.Read.All not being granted, but produces the exact same
    // "every mailbox shows no data" symptom. Set in CollectCustomerDataAsync
    // right after the mailbox usage join, surfaced via /api/customers/summary
    // so the Mailboxes sub-tab can show the actual fix instead of pointing at
    // a permission that was never the problem.
    public bool MailboxDataConcealed { get; set; }

    // Tenant-wide security data (not per-user) — JSON blobs rather than
    // related tables for the same reason as CustomerUser.LicensesJson:
    // nothing here needs to be queried/filtered server-side yet.
    // {CurrentScore, MaxScore, CreatedDateTime, ControlScores} or null if never collected/permission missing.
    public string? SecureScoreJson { get; set; }
    // Catalog of every control Secure Score can evaluate (title, remediation,
    // max score, category) — joined against SecureScoreJson's ControlScores
    // at the API layer to produce the actionable checklist the Security tab
    // shows. Changes rarely; refreshed on every collection like everything else.
    public string? SecureScoreControlProfilesJson { get; set; }
    // Array of {DisplayName, State} or null if never collected/permission missing.
    public string? ConditionalAccessPoliciesJson { get; set; }

    // Members of the built-in Global Administrator directory role — array of
    // the same user shape as GraphUserDto, or null if never collected/permission
    // missing. Needs RoleManagement.ReadWrite.Directory (already granted for
    // the EXO Global Reader self-assignment above, so this needs no new consent).
    public string? GlobalAdminsJson { get; set; }
    // Whether Security Defaults is enforced tenant-wide — needs Policy.Read.All
    // (same permission as ConditionalAccessPoliciesJson). A plain nullable bool,
    // not JSON, since it's a single fact rather than a collection — null means
    // never collected/permission missing, not "confirmed disabled".
    public bool? SecurityDefaultsEnabled { get; set; }

    // Per-domain SPF/DMARC DNS TXT lookup results — array of DnsRecordCheckDto,
    // one per verified domain, or null if never collected. Unlike everything
    // else above, this needs no Graph/EXO permission at all beyond the
    // verified-domain list already fetched for EXO (see
    // GraphAppClient.GetVerifiedDomainsAsync) — it's a live, unauthenticated
    // public DNS query, done fresh on every collection.
    public string? DnsRecordChecksJson { get; set; }

    // Exchange Online (EXO) PowerShell data — a separate collection track from
    // everything above. Graph can't expose anti-phishing/Safe Links/Safe
    // Attachments/anti-spam/anti-malware/DKIM/mail-flow config at all, so this
    // is collected via app-only Exchange Online PowerShell instead (see
    // ExoPowerShellClient). Getting here needs its own one-time setup — the
    // app's service principal has to hold the Global Reader directory role in
    // the customer's tenant — which is independent of (and can lag behind)
    // ConsentGranted above.

    // True once Spectra's service principal has been assigned the Global
    // Reader role in this tenant (see GraphAppClient.
    // EnsureGlobalReaderRoleAssignedAsync). A prerequisite for the EXO
    // PowerShell collection below, tracked separately from ConsentGranted
    // because the two steps can succeed/fail independently.
    public bool ExoRoleAssigned { get; set; }

    // Set only when an EXO PowerShell collection actually succeeds — unlike
    // LastSyncedAt, this is NOT touched on every attempt. A stale value here
    // combined with a non-null ExoLastError means "last refresh failed, this
    // is older data" rather than "never worked".
    public DateTimeOffset? ExoLastCollectedAt { get; set; }
    public string? ExoLastError { get; set; }

    // One column per EXO cmdlet collected, mirroring the Graph "one call, one
    // column" convention above. Each is null if never collected, that specific
    // cmdlet failed within an otherwise-successful run, or EXO access isn't
    // set up yet — never used to distinguish those cases from each other,
    // ExoLastError carries that detail instead.
    public string? ExoOrganizationConfigJson { get; set; }
    public string? ExoAcceptedDomainsJson { get; set; }
    public string? ExoAntiPhishPoliciesJson { get; set; }
    public string? ExoSafeLinksPoliciesJson { get; set; }
    public string? ExoSafeAttachmentPoliciesJson { get; set; }
    public string? ExoHostedContentFilterPoliciesJson { get; set; }
    public string? ExoHostedOutboundSpamFilterPoliciesJson { get; set; }
    public string? ExoMalwareFilterPoliciesJson { get; set; }
    public string? ExoDkimSigningConfigsJson { get; set; }
    public string? ExoTransportRulesJson { get; set; }
    public string? ExoSharingPoliciesJson { get; set; }
    public string? ExoHostedConnectionFilterPoliciesJson { get; set; }
    public string? ExoAdminAuditLogConfigJson { get; set; }
    public string? ExoAtpPolicyForO365Json { get; set; }
    public string? ExoRemoteDomainsJson { get; set; }
    public string? ExoMailboxAuditBypassJson { get; set; }
    // Mailboxes with direct (mailbox-property) auto-forwarding configured —
    // via the Exchange admin center or a user's own OWA setting, not an
    // inbox rule (that's CustomerUser.ForwardingRulesJson, a separate Graph-
    // sourced mechanism). Only forwarding mailboxes are collected, so this
    // is normally a short list.
    public string? ExoMailboxForwardingJson { get; set; }
    // Full Access (and other) delegate grants on any mailbox — who besides
    // the owner can open it. NT AUTHORITY\SELF and inherited entries are
    // filtered out at collection time, so a null/empty value means "no
    // delegate access found", not "not collected yet" (same convention as
    // ExoMailboxForwardingJson).
    public string? ExoMailboxPermissionsJson { get; set; }
    // Send As grants — a different permission from mailbox access above.
    public string? ExoRecipientPermissionsJson { get; set; }

    // Microsoft Purview (Security & Compliance) data — a sibling collection
    // track to the EXO PowerShell data above, via a separate PowerShell
    // session (Connect-IPPSSession, see SccPowerShellClient) but the exact
    // same certificate and Global Reader role assignment, so it's gated on
    // the same ExoRoleAssigned flag above rather than a separate column.
    public DateTimeOffset? SccLastCollectedAt { get; set; }
    public string? SccLastError { get; set; }
    public string? SccDlpPoliciesJson { get; set; }
    public string? SccRetentionPoliciesJson { get; set; }
    public string? SccAlertPoliciesJson { get; set; }

    // Azure Resource Manager data — architecturally different from
    // everything above: Graph/EXO/SCC all flow through Entra admin consent,
    // but ARM authorizes purely by Azure RBAC role assignment on a
    // subscription/resource, with no admin-consent screen involved at all.
    // The customer's Azure admin has to separately assign the Reader role to
    // Spectra's app (already present in their tenant as a service principal
    // once they've done the ordinary Graph consent step) via Access Control
    // (IAM) on their subscription(s) — see README. Only subscriptions the
    // app actually has a role on come back here, so an empty/null value
    // almost always means "Reader hasn't been assigned yet", not "no
    // subscriptions exist".
    public string? AzureSubscriptionsJson { get; set; }
    public string? AzureVirtualMachinesJson { get; set; }
    public string? AzureAppServicesJson { get; set; }
    public DateTimeOffset? AzureLastCollectedAt { get; set; }
    public string? AzureLastError { get; set; }

    // Reserved Instances / savings plans — needs a second, separate,
    // higher-privilege role (Reservations Reader) assigned at the *tenant
    // root* via a completely different Azure Portal screen (Home →
    // Reservations → Role assignment) than the subscription-scoped Reader
    // role above. A bigger, rarer ask for a customer's admin, so this
    // degrades independently — null means either never collected or that
    // role specifically isn't granted, same convention as ExoRoleAssigned
    // gating the EXO PowerShell columns above.
    public string? AzureReservationsJson { get; set; }

    // Entra App Registrations and Enterprise Applications — Graph-sourced
    // (not ARM), needs Application.Read.All, a genuinely new Graph
    // application permission on top of everything else. Existing customers
    // need to re-grant consent (Settings → Customers → Grant consent) before
    // this starts returning data, same as any other new permission added
    // mid-project.
    public string? EntraAppRegistrationsJson { get; set; }
    public string? EntraServicePrincipalsJson { get; set; }

    // SharePoint sites, tenant-wide — same Reports.Read.All permission as
    // mailbox/OneDrive usage (GraphAppClient.GetSharePointSiteUsageAsync),
    // no new consent needed. Array of {SiteId, SiteUrl, OwnerDisplayName,
    // OwnerPrincipalName, RootWebTemplate, StorageUsedBytes,
    // StorageAllocatedBytes, FileCount, ActiveFileCount, LastActivityDate}.
    public string? SharePointSitesJson { get; set; }

    // Teams, tenant-wide — needs Team.ReadBasic.All, Channel.ReadBasic.All,
    // and TeamMember.Read.All, three genuinely new Graph application
    // permissions, so existing customers need to re-grant consent before
    // this starts returning data (see Settings → Customers → Grant
    // consent). Array of {TeamId, DisplayName, Description, Visibility,
    // IsArchived, Channels: [...], Members: [...]} —
    // GraphAppClient.GetTeamsAsync.
    public string? TeamsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedByEmail { get; set; }
}
