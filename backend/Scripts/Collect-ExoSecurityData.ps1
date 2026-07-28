<#
.SYNOPSIS
    Collects a read-only snapshot of Exchange Online security configuration
    for one customer tenant, via app-only (certificate-based) auth, and emits
    it as a single JSON blob on stdout for Spectra's backend to parse.

.DESCRIPTION
    Invoked by ExoPowerShellClient.cs via `pwsh -File`. Each cmdlet is
    independently wrapped in its own try/catch so a single cmdlet failing
    (RBAC scoping oddity, throttling, a cmdlet that doesn't apply to this
    tenant's plan) doesn't sink the rest of the batch — mirrors the
    per-dataset resilience already used for the Graph collection in
    CollectCustomerDataAsync. The certificate password is read from the
    EXO_CERT_PASSWORD environment variable, never a parameter, so it never
    shows up in process listings.
#>
param(
    [Parameter(Mandatory = $true)][string]$Organization,
    [Parameter(Mandatory = $true)][string]$AppId,
    [Parameter(Mandatory = $true)][string]$CertificatePath
)

$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

$result = [ordered]@{
    OrganizationConfig               = $null
    AcceptedDomains                  = $null
    AntiPhishPolicies                = $null
    SafeLinksPolicies                = $null
    SafeAttachmentPolicies           = $null
    HostedContentFilterPolicies      = $null
    HostedOutboundSpamFilterPolicies = $null
    MalwareFilterPolicies            = $null
    DkimSigningConfigs               = $null
    TransportRules                   = $null
    SharingPolicies                  = $null
    HostedConnectionFilterPolicies   = $null
    AdminAuditLogConfig              = $null
    AtpPolicyForO365                 = $null
    RemoteDomains                    = $null
    MailboxAuditBypass               = $null
    MailboxForwarding                = $null
    MailboxPermissions               = $null
    RecipientPermissions             = $null
}

try {
    $securePassword = ConvertTo-SecureString -String $env:EXO_CERT_PASSWORD -AsPlainText -Force
    Connect-ExchangeOnline `
        -AppId $AppId `
        -CertificateFilePath $CertificatePath `
        -CertificatePassword $securePassword `
        -Organization $Organization `
        -ShowBanner:$false

    try { $result.OrganizationConfig = Get-OrganizationConfig | Select-Object OAuth2ClientProfileEnabled, AuditDisabled, MailTipsExternalRecipientsTipsEnabled } catch { }
    try { $result.AcceptedDomains = @(Get-AcceptedDomain | Select-Object DomainName, Default) } catch { }
    try { $result.AntiPhishPolicies = @(Get-AntiPhishPolicy | Select-Object Name, IsDefault, Enabled, EnableTargetedUserProtection, EnableOrganizationDomainsProtection, EnableSpoofIntelligence, EnableMailboxIntelligence, EnableMailboxIntelligenceProtection, AuthenticationFailAction, HonorDmarcPolicy) } catch { }
    try { $result.SafeLinksPolicies = @(Get-SafeLinksPolicy | Select-Object Name, IsDefault, EnableSafeLinksForEmail, EnableSafeLinksForOffice, AllowClickThrough, TrackClicks) } catch { }
    try { $result.SafeAttachmentPolicies = @(Get-SafeAttachmentPolicy | Select-Object Name, IsDefault, Enable, Action) } catch { }
    try { $result.HostedContentFilterPolicies = @(Get-HostedContentFilterPolicy | Select-Object Name, IsDefault, BulkThreshold, AllowedSenderDomains, SpamAction, HighConfidenceSpamAction, PhishSpamAction) } catch { }
    try { $result.HostedOutboundSpamFilterPolicies = @(Get-HostedOutboundSpamFilterPolicy | Select-Object Name, IsDefault, AutoForwardingMode) } catch { }
    try { $result.MalwareFilterPolicies = @(Get-MalwareFilterPolicy | Select-Object Name, IsDefault, EnableFileFilter, ZapEnabled, EnableInternalSenderAdminNotifications, EnableExternalSenderAdminNotifications) } catch { }
    try { $result.DkimSigningConfigs = @(Get-DkimSigningConfig | Select-Object Domain, Enabled) } catch { }
    try { $result.TransportRules = @(Get-TransportRule | Select-Object Name, State, Priority, Description, SetSCL, DeleteMessage) } catch { }
    try { $result.SharingPolicies = @(Get-SharingPolicy | Select-Object Name, Default, Enabled, Domains) } catch { }
    try { $result.HostedConnectionFilterPolicies = @(Get-HostedConnectionFilterPolicy | Select-Object Name, IsDefault, IPAllowList) } catch { }
    try { $result.AdminAuditLogConfig = Get-AdminAuditLogConfig | Select-Object UnifiedAuditLogIngestionEnabled } catch { }
    try { $result.AtpPolicyForO365 = Get-AtpPolicyForO365 | Select-Object EnableATPForSPOTeamsODB, EnableSafeDocs } catch { }
    try { $result.RemoteDomains = @(Get-RemoteDomain | Select-Object DomainName, AutoForwardEnabled) } catch { }
    # Can be slow on large tenants (enumerates every mailbox with a bypass
    # association) — still bounded by the overall collection timeout below,
    # and worth it: ORCA flags any bypass association as a real finding,
    # since it silently exempts a mailbox from audit logging entirely.
    try { $result.MailboxAuditBypass = @(Get-MailboxAuditBypassAssociation | Select-Object Name, AuditBypassEnabled) } catch { }
    # Mailbox-level auto-forwarding — a completely different mechanism from an
    # inbox rule (that's collected separately via Graph): this is the
    # ForwardingAddress/ForwardingSmtpAddress property set directly on the
    # mailbox, either by an admin in the Exchange admin center (Mailboxes ->
    # a user -> Mail flow -> Edit forwarding) or by the user themselves via
    # OWA's own "Forwarding" self-service setting — neither path creates an
    # inbox rule. Filtered server-side to just the mailboxes that actually
    # have it set, so this stays cheap regardless of tenant size.
    # ForwardingAddress is a recipient-identity object, not a plain string —
    # coerced to a string explicitly so ConvertTo-Json (and the C# DTO on the
    # other end, which expects a plain string) get a predictable shape rather
    # than a nested object for internal-recipient forwarding targets.
    try {
        $result.MailboxForwarding = @(Get-Mailbox -ResultSize Unlimited -Filter {ForwardingSmtpAddress -ne $null -or ForwardingAddress -ne $null} | Select-Object `
            UserPrincipalName, `
            @{Name='ForwardingAddress'; Expression={ if ($_.ForwardingAddress) { $_.ForwardingAddress.ToString() } else { $null } }}, `
            @{Name='ForwardingSmtpAddress'; Expression={ if ($_.ForwardingSmtpAddress) { $_.ForwardingSmtpAddress.ToString() } else { $null } }}, `
            DeliverToMailboxAndForward)
    } catch { }
    # Full Access (and other) delegate grants — Get-EXOMailboxPermission with
    # no -Identity returns every mailbox's permissions in one call rather
    # than needing to iterate mailboxes one at a time. NT AUTHORITY\SELF
    # (every mailbox's default grant to its own owner) and inherited entries
    # are filtered out here so what's left is only genuine, explicitly
    # granted delegate access — the kind of thing worth a client noticing.
    try {
        $result.MailboxPermissions = @(Get-EXOMailboxPermission -ResultSize Unlimited |
            Where-Object { -not $_.IsInherited -and $_.User.ToString() -ne 'NT AUTHORITY\SELF' } |
            Select-Object `
                Identity, `
                @{Name='User'; Expression={ $_.User.ToString() }}, `
                @{Name='AccessRights'; Expression={ @($_.AccessRights | ForEach-Object { $_.ToString() }) }}, `
                Deny)
    } catch { }
    # Send As grants — same one-call-for-everything shape as
    # Get-EXOMailboxPermission above, via the EXO-native cmdlet Microsoft
    # recommends over the older Get-RecipientPermission for Exchange Online.
    try {
        $result.RecipientPermissions = @(Get-EXORecipientPermission -ResultSize Unlimited |
            Where-Object { $_.Trustee.ToString() -ne 'NT AUTHORITY\SELF' } |
            Select-Object `
                Identity, `
                @{Name='Trustee'; Expression={ $_.Trustee.ToString() }}, `
                @{Name='AccessRights'; Expression={ @($_.AccessRights | ForEach-Object { $_.ToString() }) }})
    } catch { }
}
finally {
    # EXO app-only sessions are capped per app/tenant — an orphaned session
    # from a crash or timeout will eventually lock out future collections if
    # this doesn't run, so it's in `finally` rather than at the end of `try`.
    try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
}

$result | ConvertTo-Json -Depth 8 -Compress
