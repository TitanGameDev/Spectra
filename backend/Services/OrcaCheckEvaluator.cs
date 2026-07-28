namespace Spectra.Api.Services;

public record OrcaCheckResultDto(string Id, string Title, string Category, string Status, string CurrentValue, string Remediation);

// Evaluates a fixed, hand-written catalog of security checks against a
// customer's collected Exchange Online configuration — Spectra's take on the
// checks ORCA (github.com/cammurray/orca) popularized, covering the settings
// that Graph can't expose at all (anti-phishing, Safe Links, Safe
// Attachments, anti-spam/anti-malware, DKIM, mail-flow rules). Remediation
// text is written fresh, not copied from ORCA's own output — ORCA is a
// separate project under its own license.
//
// Deliberately a flat list of small private static methods rather than a
// generic rule-registration framework: adding a check later means adding one
// method and one call in Evaluate(), which is cheap enough that a DSL would
// be overengineering for this catalog's size. Pure function, no I/O — each
// check is trivially unit-testable with hand-built DTOs.
//
// Every policy-list-backed check (anti-phishing, Safe Links, Safe
// Attachments, anti-spam, anti-malware) evaluates ALL policies in the
// tenant — default and custom-scoped alike — via EvaluateAcrossPolicies,
// not just the tenant default. A tenant is only as protected as its weakest
// policy: a customer with a solid default policy but a misconfigured
// custom policy for, say, a VIP group is still exposed. DKIM (domain-keyed),
// mail-flow rules (already tenant-wide), org config, and sharing (not
// meaningfully user/group-scoped) don't fit this pattern and stay as
// single-object checks.
public static class OrcaCheckEvaluator
{
    public static List<OrcaCheckResultDto> Evaluate(
        ExoOrganizationConfigDto? organizationConfig,
        List<ExoAcceptedDomainDto>? acceptedDomains,
        List<ExoAntiPhishPolicyDto>? antiPhishPolicies,
        List<ExoSafeLinksPolicyDto>? safeLinksPolicies,
        List<ExoSafeAttachmentPolicyDto>? safeAttachmentPolicies,
        List<ExoHostedContentFilterPolicyDto>? contentFilterPolicies,
        List<ExoHostedOutboundSpamFilterPolicyDto>? outboundSpamPolicies,
        List<ExoMalwareFilterPolicyDto>? malwarePolicies,
        List<ExoDkimSigningConfigDto>? dkimConfigs,
        List<ExoTransportRuleDto>? transportRules,
        List<ExoSharingPolicyDto>? sharingPolicies,
        List<ExoHostedConnectionFilterPolicyDto>? connectionFilterPolicies,
        ExoAdminAuditLogConfigDto? adminAuditLogConfig,
        ExoAtpPolicyForO365Dto? atpPolicyForO365,
        List<ExoRemoteDomainDto>? remoteDomains,
        List<ExoMailboxAuditBypassDto>? mailboxAuditBypass,
        List<ExoMailboxForwardingDto>? mailboxForwarding)
    {
        var sharing = Default(sharingPolicies, p => p.Default);

        return
        [
            CheckImpersonationProtection(antiPhishPolicies),
            CheckSpoofIntelligence(antiPhishPolicies),
            CheckMailboxIntelligence(antiPhishPolicies),
            CheckDmarcHonored(antiPhishPolicies),
            CheckAuthFailureQuarantine(antiPhishPolicies),

            CheckSafeLinksEmail(safeLinksPolicies),
            CheckSafeLinksOffice(safeLinksPolicies),
            CheckSafeLinksClickThrough(safeLinksPolicies),
            CheckSafeLinksTrackClicks(safeLinksPolicies),

            CheckSafeAttachmentsEnabled(safeAttachmentPolicies),
            CheckSafeAttachmentsAction(safeAttachmentPolicies),
            CheckAtpForSharePointOneDriveTeams(atpPolicyForO365),
            CheckSafeDocsEnabled(atpPolicyForO365),

            CheckBulkComplaintThreshold(contentFilterPolicies),
            CheckNoDomainWideAllowList(contentFilterPolicies),
            CheckHighConfidenceSpamQuarantined(contentFilterPolicies),
            CheckPhishQuarantined(contentFilterPolicies),
            CheckSpamAtLeastJunked(contentFilterPolicies),
            CheckAutoForwardingBlocked(outboundSpamPolicies),
            CheckNoIpAllowListBypass(connectionFilterPolicies),
            CheckNoRemoteDomainAutoForward(remoteDomains),
            CheckNoExternalMailboxForwarding(mailboxForwarding, acceptedDomains),

            CheckCommonAttachmentFilter(malwarePolicies),
            CheckZapEnabled(malwarePolicies),
            CheckMalwareInternalNotifications(malwarePolicies),
            CheckMalwareExternalNotifications(malwarePolicies),

            CheckDkimEnabledForAllDomains(dkimConfigs, acceptedDomains),
            CheckTransportRulesNoBypass(transportRules),
            CheckNoSilentDelete(transportRules),

            CheckModernAuthEnabled(organizationConfig),
            CheckMailboxAuditEnabled(organizationConfig),
            CheckExternalMailTipsEnabled(organizationConfig),
            CheckUnifiedAuditLogEnabled(adminAuditLogConfig),
            CheckNoMailboxAuditBypass(mailboxAuditBypass),

            CheckSharingPolicyNotFullyOpen(sharing),
        ];
    }

    private static T? Default<T>(List<T>? policies, Func<T, bool?> isDefault) =>
        policies is null ? default : (policies.FirstOrDefault(p => isDefault(p) == true) ?? policies.FirstOrDefault());

    private static OrcaCheckResultDto Info(string id, string title, string category, string remediation) =>
        new(id, title, category, "info", "Not collected", remediation);

    private static OrcaCheckResultDto Result(string id, string title, string category, bool pass, string currentValue, string remediation) =>
        new(id, title, category, pass ? "pass" : "fail", currentValue, remediation);

    // A tenant can have any number of custom-scoped policies on top of the
    // single tenant-default one — this evaluates every policy the pass
    // predicate applies to and only passes if all of them do, naming which
    // ones don't so the failure is actionable rather than a bare "fail".
    private static OrcaCheckResultDto EvaluateAcrossPolicies<T>(
        string id, string title, string category, string remediation,
        List<T>? policies, Func<T, bool> passes, Func<T, string?> name)
    {
        if (policies is null || policies.Count == 0) return Info(id, title, category, remediation);
        var failing = policies.Where(p => !passes(p)).Select(p => name(p) ?? "(unnamed)").ToList();
        var pass = failing.Count == 0;
        var currentValue = pass
            ? $"Pass ({policies.Count}/{policies.Count} polic{(policies.Count == 1 ? "y" : "ies")})"
            : $"Fails on: {string.Join(", ", failing)} ({policies.Count - failing.Count}/{policies.Count} pass)";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    // --- Anti-phishing ---------------------------------------------------

    private static OrcaCheckResultDto CheckImpersonationProtection(List<ExoAntiPhishPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-phish-impersonation",
            "Impersonation protection is enabled",
            "Anti-phishing",
            "Turn on user and/or domain impersonation protection in every anti-phishing policy so lookalike sender names and domains get flagged.",
            policies,
            p => p.EnableTargetedUserProtection == true || p.EnableOrganizationDomainsProtection == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckSpoofIntelligence(List<ExoAntiPhishPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-phish-spoof-intelligence",
            "Spoof intelligence is enabled",
            "Anti-phishing",
            "Enable spoof intelligence in every anti-phishing policy so Defender can learn and act on senders spoofing your domains or others.",
            policies,
            p => p.EnableSpoofIntelligence == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckMailboxIntelligence(List<ExoAntiPhishPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-phish-mailbox-intelligence",
            "Mailbox intelligence is enabled",
            "Anti-phishing",
            "Enable mailbox intelligence (and mailbox intelligence protection) in every anti-phishing policy so Defender factors in each user's real communication patterns when spotting impersonation.",
            policies,
            p => p.EnableMailboxIntelligence == true && p.EnableMailboxIntelligenceProtection == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckDmarcHonored(List<ExoAntiPhishPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-phish-dmarc-honored",
            "DMARC policy is honored",
            "Anti-phishing",
            "Enable \"honor DMARC policy\" in every anti-phishing policy so mail failing a sender domain's own published DMARC policy is actually acted on, not just observed.",
            policies,
            p => p.HonorDmarcPolicy == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckAuthFailureQuarantine(List<ExoAntiPhishPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-phish-auth-fail-action",
            "Authentication failures are quarantined",
            "Anti-phishing",
            "Set every anti-phishing policy's authentication-failure action to Quarantine — moving to Junk still leaves a plausible-looking spoofed message reachable in the mailbox.",
            policies,
            p => string.Equals(p.AuthenticationFailAction, "Quarantine", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    // --- Safe Links --------------------------------------------------------

    private static OrcaCheckResultDto CheckSafeLinksEmail(List<ExoSafeLinksPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-links-email",
            "Safe Links is enabled for email",
            "Safe Links",
            "Enable Safe Links for email in every policy so links in incoming mail are checked at click time, not just at delivery.",
            policies,
            p => p.EnableSafeLinksForEmail == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckSafeLinksOffice(List<ExoSafeLinksPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-links-office",
            "Safe Links is enabled for Office apps",
            "Safe Links",
            "Enable Safe Links for Office apps in every policy so links opened from Word/Excel/PowerPoint/Teams get the same protection as links in email.",
            policies,
            p => p.EnableSafeLinksForOffice == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckSafeLinksClickThrough(List<ExoSafeLinksPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-links-click-through",
            "Users can't click through a Safe Links warning",
            "Safe Links",
            "Disable \"allow click-through\" in every Safe Links policy so a warned user can't simply bypass the block on a flagged link.",
            policies,
            p => p.AllowClickThrough != true,
            p => p.Name);

    private static OrcaCheckResultDto CheckSafeLinksTrackClicks(List<ExoSafeLinksPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-links-track-clicks",
            "Safe Links click tracking is enabled",
            "Safe Links",
            "Enable click tracking in every Safe Links policy so a suspicious/malicious link that does get clicked leaves an investigable trail.",
            policies,
            p => p.TrackClicks == true,
            p => p.Name);

    // --- Safe Attachments ----------------------------------------------

    private static OrcaCheckResultDto CheckSafeAttachmentsEnabled(List<ExoSafeAttachmentPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-attachments-enabled",
            "Safe Attachments is enabled",
            "Safe Attachments",
            "Enable every Safe Attachments policy so incoming attachments are detonated/scanned before delivery.",
            policies,
            p => p.Enable == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckSafeAttachmentsAction(List<ExoSafeAttachmentPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "safe-attachments-action",
            "Safe Attachments action actually blocks malicious files",
            "Safe Attachments",
            "Set every Safe Attachments policy's action to Block or Dynamic Delivery — Monitor-only detects malware but still delivers it to the inbox.",
            policies,
            p => string.Equals(p.Action, "Block", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Action, "DynamicDelivery", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    private static OrcaCheckResultDto CheckAtpForSharePointOneDriveTeams(ExoAtpPolicyForO365Dto? config)
    {
        const string id = "safe-attachments-spo-odb-teams";
        const string category = "Safe Attachments";
        const string title = "Safe Attachments protection covers SharePoint, OneDrive, and Teams";
        const string remediation = "Enable ATP for SharePoint, OneDrive, and Teams on the org-wide ATP policy — Safe Attachments email scanning doesn't cover files shared or uploaded directly through those apps without this.";
        if (config?.EnableATPForSPOTeamsODB is null) return Info(id, title, category, remediation);
        var pass = config.EnableATPForSPOTeamsODB == true;
        return Result(id, title, category, pass, pass ? "Enabled" : "Disabled", remediation);
    }

    private static OrcaCheckResultDto CheckSafeDocsEnabled(ExoAtpPolicyForO365Dto? config)
    {
        const string id = "safe-attachments-safe-docs";
        const string category = "Safe Attachments";
        const string title = "Safe Documents is enabled for Office desktop clients";
        const string remediation = "Enable Safe Documents on the org-wide ATP policy so files opened in Office desktop apps (Word, Excel, PowerPoint) from outside the trusted list get scanned in the cloud before being opened locally.";
        if (config?.EnableSafeDocs is null) return Info(id, title, category, remediation);
        var pass = config.EnableSafeDocs == true;
        return Result(id, title, category, pass, pass ? "Enabled" : "Disabled", remediation);
    }

    // --- Anti-spam -----------------------------------------------------

    private static OrcaCheckResultDto CheckBulkComplaintThreshold(List<ExoHostedContentFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-bulk-threshold",
            "Bulk complaint level threshold is reasonably strict",
            "Anti-spam",
            "Lower every anti-spam policy's bulk complaint level (BCL) threshold to 7 or below so noisier bulk mail gets filtered instead of reaching inboxes.",
            policies,
            p => p.BulkThreshold is not null && p.BulkThreshold <= 7,
            p => p.Name);

    private static OrcaCheckResultDto CheckNoDomainWideAllowList(List<ExoHostedContentFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-no-domain-allowlist",
            "No entire domains are allow-listed",
            "Anti-spam",
            "Remove any entries from every anti-spam policy's allowed-sender-domains list — allow-listing a whole domain lets spam and phishing from anyone at that domain skip filtering entirely, a classic misconfiguration.",
            policies,
            p => (p.AllowedSenderDomains?.Count ?? 0) == 0,
            p => p.Name);

    private static OrcaCheckResultDto CheckHighConfidenceSpamQuarantined(List<ExoHostedContentFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-high-confidence-quarantine",
            "High-confidence spam is quarantined",
            "Anti-spam",
            "Set every anti-spam policy's high-confidence-spam action to Quarantine — mail Defender is highly confident is spam shouldn't just land in Junk where it can still be opened.",
            policies,
            p => string.Equals(p.HighConfidenceSpamAction, "Quarantine", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    private static OrcaCheckResultDto CheckPhishQuarantined(List<ExoHostedContentFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-phish-quarantine",
            "Detected phishing email is quarantined",
            "Anti-spam",
            "Set every anti-spam policy's phishing action to Quarantine, not Junk — phishing mail is a materially different risk from ordinary spam and shouldn't be treated the same.",
            policies,
            p => string.Equals(p.PhishSpamAction, "Quarantine", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    private static OrcaCheckResultDto CheckSpamAtLeastJunked(List<ExoHostedContentFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-spam-action",
            "Spam is at least moved to Junk, not delivered to the inbox",
            "Anti-spam",
            "Set every anti-spam policy's spam action to Quarantine or MoveToJmf — letting detected spam land directly in the inbox defeats the point of running the filter at all.",
            policies,
            p => string.Equals(p.SpamAction, "Quarantine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.SpamAction, "MoveToJmf", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    private static OrcaCheckResultDto CheckAutoForwardingBlocked(List<ExoHostedOutboundSpamFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-auto-forwarding",
            "Automatic external forwarding is blocked tenant-wide",
            "Anti-spam",
            "Set every outbound spam filter policy's auto-forwarding mode to Off — unrestricted auto-forwarding is a common way compromised mailboxes exfiltrate mail to an attacker.",
            policies,
            p => string.Equals(p.AutoForwardingMode, "Off", StringComparison.OrdinalIgnoreCase),
            p => p.Name);

    private static OrcaCheckResultDto CheckNoIpAllowListBypass(List<ExoHostedConnectionFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-spam-no-ip-allowlist",
            "No tenant-wide IP allow-list bypassing spam filtering",
            "Anti-spam",
            "Remove entries from every connection filter policy's IP allow list — an allow-listed IP skips spam filtering entirely for anything it sends, one of the most common ways a tenant ends up wide open despite otherwise-good settings.",
            policies,
            p => (p.IPAllowList?.Count ?? 0) == 0,
            p => p.Name);

    private static OrcaCheckResultDto CheckNoRemoteDomainAutoForward(List<ExoRemoteDomainDto>? remoteDomains)
    {
        const string id = "anti-spam-remote-domain-auto-forward";
        const string category = "Anti-spam";
        const string title = "No remote domain allows automatic forwarding";
        const string remediation = "Disable automatic forwarding on the flagged remote domain — this is the other control point (alongside the outbound spam filter's auto-forwarding setting) Exchange uses to allow mail to auto-forward externally, and a compromised mailbox only needs one of them left open to exfiltrate mail.";
        if (remoteDomains is null) return Info(id, title, category, remediation);

        var offending = remoteDomains
            .Where(d => d.AutoForwardEnabled == true)
            .Select(d => d.DomainName ?? "(unnamed)")
            .ToList();

        var pass = offending.Count == 0;
        return Result(id, title, category, pass, pass ? "None found" : string.Join(", ", offending), remediation);
    }

    // Mailbox-level auto-forwarding (ForwardingAddress/ForwardingSmtpAddress
    // set directly on the mailbox, via the Exchange admin center or a user's
    // own OWA "Forwarding" setting) — a different mechanism from both the
    // tenant-wide controls above (outbound spam filter auto-forwarding mode,
    // remote domain auto-forward) and from an inbox rule (collected
    // separately via Graph). Internal-to-internal forwarding (e.g. a
    // shared-mailbox setup) is common and benign, so only forwarding to a
    // domain the tenant doesn't own is flagged.
    private static OrcaCheckResultDto CheckNoExternalMailboxForwarding(List<ExoMailboxForwardingDto>? forwarding, List<ExoAcceptedDomainDto>? acceptedDomains)
    {
        const string id = "anti-spam-no-external-mailbox-forwarding";
        const string category = "Anti-spam";
        const string title = "No mailbox forwards to an external domain";
        const string remediation = "Remove the forwarding address from the flagged mailbox(es) in the Exchange admin center (Mailboxes → the mailbox → Mail flow → Edit forwarding) — auto-forwarding to an address outside the tenant's own domains is one of the most common ways a compromised mailbox exfiltrates mail, and is easy to miss since it's set per-mailbox, not in a policy.";
        if (forwarding is null) return Info(id, title, category, remediation);
        if (forwarding.Count == 0) return Result(id, title, category, true, "None found", remediation);

        var ownedDomains = (acceptedDomains ?? [])
            .Where(d => d.DomainName is not null)
            .Select(d => d.DomainName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offending = forwarding
            .Where(f => ForwardsExternally(f, ownedDomains))
            .Select(f => f.UserPrincipalName ?? "(unknown mailbox)")
            .ToList();

        var pass = offending.Count == 0;
        return Result(id, title, category, pass, pass ? "None found" : string.Join(", ", offending), remediation);
    }

    private static bool ForwardsExternally(ExoMailboxForwardingDto forwarding, HashSet<string> ownedDomains)
    {
        var domain = ExtractDomain(forwarding.ForwardingSmtpAddress ?? forwarding.ForwardingAddress);
        // ForwardingAddress is often an internal recipient identity (a name
        // or GUID, no @domain) rather than an address — with no resolvable
        // domain to check, it's assumed internal rather than flagged.
        return domain is not null && !ownedDomains.Contains(domain);
    }

    private static string? ExtractDomain(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        // ForwardingSmtpAddress commonly comes through as "smtp:user@domain.com" —
        // strip that prefix before pulling out the domain.
        var atIdx = address.LastIndexOf('@');
        return atIdx >= 0 && atIdx < address.Length - 1 ? address[(atIdx + 1)..] : null;
    }

    // --- Anti-malware ----------------------------------------------------

    private static OrcaCheckResultDto CheckCommonAttachmentFilter(List<ExoMalwareFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-malware-file-filter",
            "Common attachment-type filtering is enabled",
            "Anti-malware",
            "Enable the common attachment types filter in every anti-malware policy to block high-risk file types (executables, scripts) outright, regardless of content scanning.",
            policies,
            p => p.EnableFileFilter == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckZapEnabled(List<ExoMalwareFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-malware-zap",
            "Zero-hour auto purge is enabled",
            "Anti-malware",
            "Enable zero-hour auto purge (ZAP) in every anti-malware policy so mail found malicious after delivery gets retroactively removed from inboxes.",
            policies,
            p => p.ZapEnabled == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckMalwareInternalNotifications(List<ExoMalwareFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-malware-internal-notify",
            "Admins are notified on internal-sender malware detection",
            "Anti-malware",
            "Enable internal-sender admin notifications in every anti-malware policy so a mailbox sending malware internally (a strong compromise signal) doesn't go unnoticed.",
            policies,
            p => p.EnableInternalSenderAdminNotifications == true,
            p => p.Name);

    private static OrcaCheckResultDto CheckMalwareExternalNotifications(List<ExoMalwareFilterPolicyDto>? policies) =>
        EvaluateAcrossPolicies(
            "anti-malware-external-notify",
            "Admins are notified on external/outbound malware detection",
            "Anti-malware",
            "Enable external-sender admin notifications in every anti-malware policy so outbound malware (e.g. from a compromised mailbox sending to external recipients) doesn't go unnoticed.",
            policies,
            p => p.EnableExternalSenderAdminNotifications == true,
            p => p.Name);

    // --- DKIM ------------------------------------------------------------

    private static OrcaCheckResultDto CheckDkimEnabledForAllDomains(List<ExoDkimSigningConfigDto>? dkimConfigs, List<ExoAcceptedDomainDto>? acceptedDomains)
    {
        const string id = "dkim-enabled";
        const string category = "Domains";
        const string title = "DKIM is enabled for every accepted domain";
        const string remediation = "Enable DKIM signing for any accepted domain missing it — unsigned mail is easier to spoof and more likely to be marked as spam by other providers.";
        if (dkimConfigs is null || acceptedDomains is null) return Info(id, title, category, remediation);

        var enabledDomains = dkimConfigs
            .Where(d => d.Enabled == true && d.Domain is not null)
            .Select(d => d.Domain!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = acceptedDomains
            .Where(d => d.DomainName is not null && !enabledDomains.Contains(d.DomainName))
            .Select(d => d.DomainName!)
            .ToList();

        var pass = missing.Count == 0;
        return Result(id, title, category, pass, pass ? "All domains signed" : $"Missing: {string.Join(", ", missing)}", remediation);
    }

    // --- Mail flow ---------------------------------------------------------

    private static OrcaCheckResultDto CheckTransportRulesNoBypass(List<ExoTransportRuleDto>? rules)
    {
        const string id = "transport-rules-no-bypass";
        const string category = "Mail flow";
        const string title = "No mail flow rule bypasses spam/malware filtering tenant-wide";
        const string remediation = "Remove or narrow the flagged rule — a transport rule that sets the spam confidence level (SCL) to -1 skips spam AND malware filtering entirely for whatever it matches, which is one of the most common ways a tenant ends up wide open despite otherwise-good Defender settings. If it's meant to whitelist a specific trusted sender, scope it as tightly as possible rather than tenant-wide.";
        if (rules is null) return Info(id, title, category, remediation);

        var offending = rules
            .Where(r => string.Equals(r.State, "Enabled", StringComparison.OrdinalIgnoreCase) && r.SetSCL == "-1")
            .Select(r => r.Name ?? "(unnamed rule)")
            .ToList();

        var pass = offending.Count == 0;
        return Result(id, title, category, pass, pass ? "None found" : string.Join(", ", offending), remediation);
    }

    private static OrcaCheckResultDto CheckNoSilentDelete(List<ExoTransportRuleDto>? rules)
    {
        const string id = "transport-rules-no-silent-delete";
        const string category = "Mail flow";
        const string title = "No mail flow rule silently deletes messages tenant-wide";
        const string remediation = "Remove or narrow the flagged rule — a transport rule that deletes messages leaves no trace for the sender or recipient, which can hide legitimate mail loss (misconfiguration) as easily as it blocks something unwanted. Prefer quarantining or redirecting over silent deletion.";
        if (rules is null) return Info(id, title, category, remediation);

        var offending = rules
            .Where(r => string.Equals(r.State, "Enabled", StringComparison.OrdinalIgnoreCase) && r.DeleteMessage == true)
            .Select(r => r.Name ?? "(unnamed rule)")
            .ToList();

        var pass = offending.Count == 0;
        return Result(id, title, category, pass, pass ? "None found" : string.Join(", ", offending), remediation);
    }

    // --- Org config --------------------------------------------------------

    private static OrcaCheckResultDto CheckModernAuthEnabled(ExoOrganizationConfigDto? config)
    {
        const string id = "org-modern-auth";
        const string category = "Organization";
        const string title = "Modern authentication is enabled";
        const string remediation = "Enable OAuth2ClientProfileEnabled on the organization config — without modern auth, clients fall back to legacy basic-auth protocols that can't enforce MFA or Conditional Access.";
        if (config?.OAuth2ClientProfileEnabled is null) return Info(id, title, category, remediation);
        var pass = config.OAuth2ClientProfileEnabled == true;
        return Result(id, title, category, pass, pass ? "Enabled" : "Disabled", remediation);
    }

    private static OrcaCheckResultDto CheckMailboxAuditEnabled(ExoOrganizationConfigDto? config)
    {
        const string id = "org-mailbox-audit";
        const string category = "Organization";
        const string title = "Mailbox auditing is not disabled tenant-wide";
        const string remediation = "Ensure mailbox audit logging isn't disabled at the organization level — it's what lets you investigate what actually happened in a mailbox after a compromise.";
        if (config?.AuditDisabled is null) return Info(id, title, category, remediation);
        var pass = config.AuditDisabled != true;
        return Result(id, title, category, pass, config.AuditDisabled == true ? "Disabled" : "Enabled", remediation);
    }

    private static OrcaCheckResultDto CheckExternalMailTipsEnabled(ExoOrganizationConfigDto? config)
    {
        const string id = "org-external-mailtips";
        const string category = "Organization";
        const string title = "External-recipient MailTips are enabled";
        const string remediation = "Enable external-recipient MailTips on the organization config so users get a warning before sending mail to an external address — a small but real speed bump against misdirected mail and some phishing/BEC scenarios.";
        if (config?.MailTipsExternalRecipientsTipsEnabled is null) return Info(id, title, category, remediation);
        var pass = config.MailTipsExternalRecipientsTipsEnabled == true;
        return Result(id, title, category, pass, pass ? "Enabled" : "Disabled", remediation);
    }

    private static OrcaCheckResultDto CheckUnifiedAuditLogEnabled(ExoAdminAuditLogConfigDto? config)
    {
        const string id = "org-unified-audit-log";
        const string category = "Organization";
        const string title = "Unified Audit Log ingestion is enabled";
        const string remediation = "Enable Unified Audit Log ingestion — without it, there's no searchable record of user and admin activity across Exchange, SharePoint, Teams, and more, which is often the first thing needed when investigating an incident.";
        if (config?.UnifiedAuditLogIngestionEnabled is null) return Info(id, title, category, remediation);
        var pass = config.UnifiedAuditLogIngestionEnabled == true;
        return Result(id, title, category, pass, pass ? "Enabled" : "Disabled", remediation);
    }

    private static OrcaCheckResultDto CheckNoMailboxAuditBypass(List<ExoMailboxAuditBypassDto>? associations)
    {
        const string id = "org-no-mailbox-audit-bypass";
        const string category = "Organization";
        const string title = "No mailbox bypasses audit logging";
        const string remediation = "Remove the flagged mailbox audit bypass association — a mailbox with audit bypass enabled leaves no record of activity on it at all, no matter what the tenant-wide audit setting is. This is easy to lose track of since it's set per-mailbox, not in a policy.";
        if (associations is null) return Info(id, title, category, remediation);

        var offending = associations
            .Where(a => a.AuditBypassEnabled == true)
            .Select(a => a.Name ?? "(unnamed)")
            .ToList();

        var pass = offending.Count == 0;
        return Result(id, title, category, pass, pass ? "None found" : string.Join(", ", offending), remediation);
    }

    // --- Sharing -------------------------------------------------------

    private static OrcaCheckResultDto CheckSharingPolicyNotFullyOpen(ExoSharingPolicyDto? policy)
    {
        const string id = "sharing-not-fully-open";
        const string category = "Sharing";
        const string title = "Default sharing policy doesn't fully open calendars to everyone";
        const string remediation = "Narrow the default sharing policy's wildcard (\"*\") domain entry, or restrict what it shares, so free/busy and calendar details aren't handed to every external organization by default.";
        if (policy?.Domains is null) return Info(id, title, category, remediation);
        var wideOpenEntry = policy.Domains.FirstOrDefault(d => d.StartsWith('*'));
        var pass = wideOpenEntry is null;
        return Result(id, title, category, pass, pass ? "No wildcard domain entry" : wideOpenEntry!, remediation);
    }
}
