namespace Spectra.Api.Services;

// Evaluates DLP/retention/alert policy checks against a customer's collected
// Security & Compliance (Microsoft Purview) data — Spectra's take on the
// data-loss-prevention and compliance side of ORCA's coverage, sourced from
// Security & Compliance PowerShell (see SccPowerShellClient) rather than
// Exchange Online PowerShell or Graph. Alert checks deliberately key off
// Severity/Disabled/NotifyUser rather than hardcoding specific built-in
// policy names, since that applies uniformly to any tenant's actual
// configured policies (custom or built-in) without risking a typo silently
// making a check never fire.
public static class SccCheckEvaluator
{
    public static List<OrcaCheckResultDto> Evaluate(
        List<SccDlpPolicyDto>? dlpPolicies,
        List<SccRetentionPolicyDto>? retentionPolicies,
        List<SccAlertPolicyDto>? alertPolicies)
    {
        return
        [
            CheckDlpEnabled(dlpPolicies),
            CheckDlpNotStuckInTest(dlpPolicies),
            CheckRetentionEnabled(retentionPolicies),
            CheckRetentionNotStuckInTest(retentionPolicies),
            CheckHighSeverityAlertsNotDisabled(alertPolicies),
            CheckHighSeverityAlertsNotify(alertPolicies),
        ];
    }

    private static OrcaCheckResultDto Info(string id, string title, string category, string remediation) =>
        new(id, title, category, "info", "Not collected", remediation);

    private static OrcaCheckResultDto Result(string id, string title, string category, bool pass, string currentValue, string remediation) =>
        new(id, title, category, pass ? "pass" : "fail", currentValue, remediation);

    private static OrcaCheckResultDto CheckDlpEnabled(List<SccDlpPolicyDto>? policies)
    {
        const string id = "scc-dlp-enabled";
        const string category = "Compliance";
        const string title = "At least one DLP policy is enabled";
        const string remediation = "Create and enable a Data Loss Prevention policy in the Microsoft Purview compliance portal — without one, there's no automated protection against sensitive data (credit card numbers, SSNs, etc.) leaving the tenant via email, SharePoint, or OneDrive.";
        if (policies is null || policies.Count == 0) return Info(id, title, category, remediation);

        var pass = policies.Any(p => p.Enabled == true);
        var currentValue = pass ? $"{policies.Count(p => p.Enabled == true)}/{policies.Count} enabled" : "No DLP policies are enabled";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckDlpNotStuckInTest(List<SccDlpPolicyDto>? policies)
    {
        const string id = "scc-dlp-not-test-mode";
        const string category = "Compliance";
        const string title = "No enabled DLP policy is stuck in Test mode";
        const string remediation = "Move the flagged DLP policy from Test to Enforce once its test results look right — a policy left in Test mode indefinitely never actually blocks or restricts anything, it only simulates.";
        var enabled = policies?.Where(p => p.Enabled == true).ToList();
        if (enabled is null || enabled.Count == 0) return Info(id, title, category, remediation);

        var stuck = enabled.Where(p => string.Equals(p.Mode, "Test", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name ?? "(unnamed)").ToList();
        var pass = stuck.Count == 0;
        var currentValue = pass ? $"All {enabled.Count} enabled polic{(enabled.Count == 1 ? "y" : "ies")} out of Test mode" : $"Stuck in Test: {string.Join(", ", stuck)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckRetentionEnabled(List<SccRetentionPolicyDto>? policies)
    {
        const string id = "scc-retention-enabled";
        const string category = "Compliance";
        const string title = "At least one retention policy is enabled";
        const string remediation = "Create and enable a retention policy in the Microsoft Purview compliance portal — without one, there's no organization-wide control over how long content is kept or when it's deleted, which is both a compliance gap and a legal-discovery risk.";
        if (policies is null || policies.Count == 0) return Info(id, title, category, remediation);

        var pass = policies.Any(p => p.Enabled == true);
        var currentValue = pass ? $"{policies.Count(p => p.Enabled == true)}/{policies.Count} enabled" : "No retention policies are enabled";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckRetentionNotStuckInTest(List<SccRetentionPolicyDto>? policies)
    {
        const string id = "scc-retention-not-test-mode";
        const string category = "Compliance";
        const string title = "No enabled retention policy is stuck in Test mode";
        const string remediation = "Move the flagged retention policy from Test to Enforce once its test results look right — a policy left in Test mode indefinitely never actually retains or deletes anything, it only simulates.";
        var enabled = policies?.Where(p => p.Enabled == true).ToList();
        if (enabled is null || enabled.Count == 0) return Info(id, title, category, remediation);

        var stuck = enabled.Where(p => string.Equals(p.Mode, "Test", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name ?? "(unnamed)").ToList();
        var pass = stuck.Count == 0;
        var currentValue = pass ? $"All {enabled.Count} enabled polic{(enabled.Count == 1 ? "y" : "ies")} out of Test mode" : $"Stuck in Test: {string.Join(", ", stuck)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckHighSeverityAlertsNotDisabled(List<SccAlertPolicyDto>? policies)
    {
        const string id = "scc-high-severity-alerts-enabled";
        const string category = "Compliance";
        const string title = "No high-severity alert policy is disabled";
        const string remediation = "Re-enable the flagged high-severity alert polic(y/ies) in the Microsoft Purview compliance portal — a disabled high-severity alert means a genuinely serious event (e.g. malware campaign, admin privilege elevation) can happen with nobody notified.";
        var highSeverity = policies?.Where(p => string.Equals(p.Severity, "High", StringComparison.OrdinalIgnoreCase)).ToList();
        if (highSeverity is null || highSeverity.Count == 0) return Info(id, title, category, remediation);

        var disabled = highSeverity.Where(p => p.Disabled == true).Select(p => p.Name ?? "(unnamed)").ToList();
        var pass = disabled.Count == 0;
        var currentValue = pass ? $"All {highSeverity.Count} high-severity polic{(highSeverity.Count == 1 ? "y" : "ies")} enabled" : $"Disabled: {string.Join(", ", disabled)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckHighSeverityAlertsNotify(List<SccAlertPolicyDto>? policies)
    {
        const string id = "scc-high-severity-alerts-notify";
        const string category = "Compliance";
        const string title = "Every enabled high-severity alert policy notifies someone";
        const string remediation = "Add a recipient to the flagged high-severity alert polic(y/ies) — an alert that fires but notifies nobody is functionally the same as no alert at all.";
        var active = policies?.Where(p => string.Equals(p.Severity, "High", StringComparison.OrdinalIgnoreCase) && p.Disabled != true).ToList();
        if (active is null || active.Count == 0) return Info(id, title, category, remediation);

        var silent = active.Where(p => p.NotifyUser is null || p.NotifyUser.Count == 0).Select(p => p.Name ?? "(unnamed)").ToList();
        var pass = silent.Count == 0;
        var currentValue = pass ? $"All {active.Count} active high-severity polic{(active.Count == 1 ? "y" : "ies")} notify someone" : $"No recipients: {string.Join(", ", silent)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }
}
