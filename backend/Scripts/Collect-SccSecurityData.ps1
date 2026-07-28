<#
.SYNOPSIS
    Collects a read-only snapshot of Security & Compliance (Microsoft Purview)
    configuration for one customer tenant, via app-only (certificate-based)
    auth, and emits it as a single JSON blob on stdout for Spectra's backend
    to parse.

.DESCRIPTION
    Invoked by SccPowerShellClient.cs via `pwsh -File`. A sibling to
    Collect-ExoSecurityData.ps1 rather than a merge into it — Connect-IPPSSession
    is a separate session against a separate endpoint from Connect-ExchangeOnline,
    and loading both modules' cmdlets in one session risks name collisions
    (e.g. Get-OrganizationConfig exists in both). Uses the same certificate
    and app registration as Exchange Online PowerShell — Exchange.ManageAsApp
    and the Global Reader role already assigned for that also cover Security &
    Compliance PowerShell, so there's no separate access grant to wait on here.
    Each cmdlet is independently wrapped in its own try/catch, same resilience
    pattern as the EXO script. The certificate password is read from the
    SCC_CERT_PASSWORD environment variable, never a parameter, so it never
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
    DlpPolicies       = $null
    RetentionPolicies = $null
    AlertPolicies     = $null
}

try {
    $securePassword = ConvertTo-SecureString -String $env:SCC_CERT_PASSWORD -AsPlainText -Force
    Connect-IPPSSession `
        -AppId $AppId `
        -CertificateFilePath $CertificatePath `
        -CertificatePassword $securePassword `
        -Organization $Organization `
        -ShowBanner:$false

    try { $result.DlpPolicies = @(Get-DlpCompliancePolicy | Select-Object Name, Enabled, Mode) } catch { }
    try { $result.RetentionPolicies = @(Get-RetentionCompliancePolicy | Select-Object Name, Enabled, Mode) } catch { }
    try { $result.AlertPolicies = @(Get-ProtectionAlert | Select-Object Name, Category, Severity, Disabled, NotifyUser) } catch { }
}
finally {
    # Disconnect-ExchangeOnline tears down IPPS sessions too — there's no
    # separate Disconnect-IPPSSession cmdlet. In `finally` for the same reason
    # as the EXO script: an orphaned session eventually locks out future
    # collections for this app/tenant if it isn't cleaned up.
    try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
}

$result | ConvertTo-Json -Depth 8 -Compress
