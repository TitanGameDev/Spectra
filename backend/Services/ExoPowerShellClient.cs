using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Spectra.Api.Services;

public record ExoOrganizationConfigDto(bool? OAuth2ClientProfileEnabled, bool? AuditDisabled, bool? MailTipsExternalRecipientsTipsEnabled);
public record ExoAcceptedDomainDto(string? DomainName, bool? Default);
public record ExoAntiPhishPolicyDto(
    string? Name,
    bool? IsDefault,
    bool? Enabled,
    bool? EnableTargetedUserProtection,
    bool? EnableOrganizationDomainsProtection,
    bool? EnableSpoofIntelligence,
    bool? EnableMailboxIntelligence,
    bool? EnableMailboxIntelligenceProtection,
    string? AuthenticationFailAction,
    bool? HonorDmarcPolicy);
public record ExoSafeLinksPolicyDto(
    string? Name,
    bool? IsDefault,
    bool? EnableSafeLinksForEmail,
    bool? EnableSafeLinksForOffice,
    bool? AllowClickThrough,
    bool? TrackClicks);
public record ExoSafeAttachmentPolicyDto(string? Name, bool? IsDefault, bool? Enable, string? Action);
public record ExoHostedContentFilterPolicyDto(
    string? Name,
    bool? IsDefault,
    int? BulkThreshold,
    List<string>? AllowedSenderDomains,
    string? SpamAction,
    string? HighConfidenceSpamAction,
    string? PhishSpamAction);
public record ExoHostedOutboundSpamFilterPolicyDto(string? Name, bool? IsDefault, string? AutoForwardingMode);
public record ExoMalwareFilterPolicyDto(
    string? Name,
    bool? IsDefault,
    bool? EnableFileFilter,
    bool? ZapEnabled,
    bool? EnableInternalSenderAdminNotifications,
    bool? EnableExternalSenderAdminNotifications);
public record ExoDkimSigningConfigDto(string? Domain, bool? Enabled);
public record ExoTransportRuleDto(string? Name, string? State, int? Priority, string? Description, string? SetSCL, bool? DeleteMessage);
public record ExoSharingPolicyDto(string? Name, bool? Default, bool? Enabled, List<string>? Domains);
public record ExoHostedConnectionFilterPolicyDto(string? Name, bool? IsDefault, List<string>? IPAllowList);
public record ExoAdminAuditLogConfigDto(bool? UnifiedAuditLogIngestionEnabled);
public record ExoAtpPolicyForO365Dto(bool? EnableATPForSPOTeamsODB, bool? EnableSafeDocs);
public record ExoRemoteDomainDto(string? DomainName, bool? AutoForwardEnabled);
public record ExoMailboxAuditBypassDto(string? Name, bool? AuditBypassEnabled);

// Mailbox-level auto-forwarding — a mailbox property (set via the Exchange
// admin center's Mail flow > Edit forwarding panel, or a user's own OWA
// "Forwarding" self-service setting), not an inbox rule. Only mailboxes that
// actually have one of these set are collected (see the -Filter on
// Get-Mailbox in Collect-ExoSecurityData.ps1), so this list is normally
// short or empty.
public record ExoMailboxForwardingDto(
    string? UserPrincipalName,
    string? ForwardingAddress,
    string? ForwardingSmtpAddress,
    bool? DeliverToMailboxAndForward);

// Full Access (and other) delegate grants on a mailbox — who besides the
// owner can open it. Excludes NT AUTHORITY\SELF (every mailbox's own
// default self-grant) and inherited entries, so only explicit, meaningful
// delegate access shows up (see Collect-ExoSecurityData.ps1's filtering).
public record ExoMailboxPermissionDto(string? Identity, string? User, List<string>? AccessRights, bool? Deny);

// Send As grants — a different permission from mailbox access above: lets a
// delegate send mail that appears to come from the mailbox, without being
// able to open/read it.
public record ExoRecipientPermissionDto(string? Identity, string? Trustee, List<string>? AccessRights);

public record ExoCollectionResultDto(
    ExoOrganizationConfigDto? OrganizationConfig,
    List<ExoAcceptedDomainDto>? AcceptedDomains,
    List<ExoAntiPhishPolicyDto>? AntiPhishPolicies,
    List<ExoSafeLinksPolicyDto>? SafeLinksPolicies,
    List<ExoSafeAttachmentPolicyDto>? SafeAttachmentPolicies,
    List<ExoHostedContentFilterPolicyDto>? HostedContentFilterPolicies,
    List<ExoHostedOutboundSpamFilterPolicyDto>? HostedOutboundSpamFilterPolicies,
    List<ExoMalwareFilterPolicyDto>? MalwareFilterPolicies,
    List<ExoDkimSigningConfigDto>? DkimSigningConfigs,
    List<ExoTransportRuleDto>? TransportRules,
    List<ExoSharingPolicyDto>? SharingPolicies,
    List<ExoHostedConnectionFilterPolicyDto>? HostedConnectionFilterPolicies,
    ExoAdminAuditLogConfigDto? AdminAuditLogConfig,
    ExoAtpPolicyForO365Dto? AtpPolicyForO365,
    List<ExoRemoteDomainDto>? RemoteDomains,
    List<ExoMailboxAuditBypassDto>? MailboxAuditBypass,
    List<ExoMailboxForwardingDto>? MailboxForwarding,
    List<ExoMailboxPermissionDto>? MailboxPermissions,
    List<ExoRecipientPermissionDto>? RecipientPermissions);

// Exchange Online access isn't set up yet for this tenant (Global Reader
// hasn't propagated, or genuinely hasn't been assigned) — distinguishes this
// "not ready" case from a real bug, same role GraphPermissionDeniedException
// plays for the Graph collection.
public class ExoAccessException(string message) : Exception(message);

// Anything else: pwsh missing, the ExchangeOnlineManagement module not
// installed, the certificate missing/misconfigured, a timeout, or a cmdlet
// batch that didn't produce parseable JSON.
public class ExoCollectionException(string message) : Exception(message);

// Collects Exchange Online security configuration via app-only (certificate)
// PowerShell auth — the only way to reach anti-phishing/Safe Links/Safe
// Attachments/anti-spam/anti-malware/DKIM/mail-flow settings, none of which
// Graph exposes at all. Shells out to `pwsh` rather than embedding a
// PowerShell SDK, since the ExchangeOnlineManagement module's own dependency
// surface is large and this way it's just another thing installed on the
// host (see README), not baked into the app.
public class ExoPowerShellClient(IConfiguration configuration, ILogger<ExoPowerShellClient> logger)
{
    // 180s rather than the original 90s — Get-MailboxAuditBypassAssociation
    // enumerates every mailbox with a bypass association, and
    // Get-EXOMailboxPermission/Get-EXORecipientPermission (added later)
    // return tenant-wide permission data in one call each but can still be
    // meaningfully slower than the rest of the batch on a large tenant.
    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(180);

    public async Task<ExoCollectionResultDto> CollectAsync(string organizationDomain, CancellationToken ct = default)
    {
        var appId = configuration["AzureAd:ClientId"];
        var certPath = configuration["Exo:CertificatePath"];
        var certPassword = configuration["Exo:CertificatePassword"];

        if (string.IsNullOrEmpty(appId))
        {
            throw new ExoCollectionException("AzureAd:ClientId is not configured.");
        }
        if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(certPassword))
        {
            throw new ExoCollectionException("Exo:CertificatePath / Exo:CertificatePassword are not configured — see README.");
        }
        if (!File.Exists(certPath))
        {
            throw new ExoCollectionException($"EXO certificate not found at {certPath} — see README.");
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "Collect-ExoSecurityData.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new ExoCollectionException($"EXO collection script not found at {scriptPath}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-Organization");
        psi.ArgumentList.Add(organizationDomain);
        psi.ArgumentList.Add("-AppId");
        psi.ArgumentList.Add(appId);
        psi.ArgumentList.Add("-CertificatePath");
        psi.ArgumentList.Add(certPath);
        // Only the secret goes via the environment — never a CLI arg, which
        // would be visible to anything that can list this host's processes.
        psi.Environment["EXO_CERT_PASSWORD"] = certPassword;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new ExoCollectionException("pwsh (PowerShell 7) isn't installed on this server, or isn't on PATH — see README.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(CollectionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new ExoCollectionException($"Exchange Online collection timed out after {CollectionTimeout.TotalSeconds:0}s.");
        }

        var stderrText = stderr.ToString();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderrText))
        {
            ClassifyAndThrow(stderrText, process.ExitCode);
        }

        var stdoutText = stdout.ToString().Trim();
        try
        {
            var result = JsonSerializer.Deserialize<ExoCollectionResultDto>(stdoutText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (result is null)
            {
                throw new ExoCollectionException("Exchange Online collection returned no data.");
            }
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse EXO collection output for {Organization}", organizationDomain);
            throw new ExoCollectionException($"Exchange Online collection returned unparseable output: {ex.Message}");
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have already exited.
        }
    }

    private static void ClassifyAndThrow(string stderrText, int exitCode)
    {
        if (stderrText.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("does not have sufficient permission", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("Unable to acquire token", StringComparison.OrdinalIgnoreCase)
            || (stderrText.Contains("AADSTS", StringComparison.OrdinalIgnoreCase) && stderrText.Contains("consent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExoAccessException($"Exchange Online access denied — {Summarize(stderrText)}");
        }

        if (stderrText.Contains("ExchangeOnlineManagement", StringComparison.OrdinalIgnoreCase)
            && (stderrText.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                || stderrText.Contains("is not installed", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExoCollectionException("ExchangeOnlineManagement module not installed — run Install-Module ExchangeOnlineManagement -Scope AllUsers.");
        }

        throw new ExoCollectionException($"Exchange Online collection failed (exit {exitCode}) — {Summarize(stderrText)}");
    }

    // PowerShell error streams are often multi-line with a lot of stack-trace
    // noise — keep only the first non-empty line for the message that ends up
    // in ExoLastError, since that's what's actually shown to users.
    private static string Summarize(string stderrText)
    {
        var firstLine = stderrText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "unknown error" : firstLine;
    }
}
