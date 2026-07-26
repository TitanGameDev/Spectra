namespace Spectra.Api.Services;

// Evaluates a small catalog of identity/RBAC checks against a customer's
// collected Graph directory data — Spectra's take on ORCA's "Role Based
// Access Control" category, but sourced from Microsoft Graph rather than
// Exchange Online PowerShell (see OrcaCheckEvaluator for the EXO-sourced
// catalog — these share the same OrcaCheckResultDto shape and are rendered
// through the same UI, just under a different sub-tab). Needs no setup
// beyond what's already granted for the EXO Global Reader self-assignment
// (RoleManagement.ReadWrite.Directory) and Conditional Access
// (Policy.Read.All) — no new consent, no PowerShell, no waiting for role
// propagation.
public static class DirectoryCheckEvaluator
{
    public static List<OrcaCheckResultDto> Evaluate(
        List<GraphUserDto>? globalAdmins,
        bool? securityDefaultsEnabled,
        List<GraphConditionalAccessPolicyDto>? conditionalAccessPolicies,
        IReadOnlyDictionary<string, bool?> mfaRegisteredByGraphUserId)
    {
        return
        [
            CheckGlobalAdminCount(globalAdmins),
            CheckGlobalAdminsHaveMfa(globalAdmins, mfaRegisteredByGraphUserId),
            CheckMfaEnforced(securityDefaultsEnabled, conditionalAccessPolicies),
        ];
    }

    private static OrcaCheckResultDto Info(string id, string title, string category, string remediation) =>
        new(id, title, category, "info", "Not collected", remediation);

    private static OrcaCheckResultDto Result(string id, string title, string category, bool pass, string currentValue, string remediation) =>
        new(id, title, category, pass ? "pass" : "fail", currentValue, remediation);

    private static OrcaCheckResultDto CheckGlobalAdminCount(List<GraphUserDto>? globalAdmins)
    {
        const string id = "identity-global-admin-count";
        const string category = "Identity";
        const string title = "Global Administrator count is reasonable";
        const string remediation = "Microsoft recommends keeping 2–4 Global Administrators — enough to avoid a single point of failure, few enough to keep the blast radius of a compromised admin account small. Use scoped admin roles (e.g. Exchange Administrator, User Administrator) for anything that doesn't genuinely need tenant-wide control.";
        if (globalAdmins is null) return Info(id, title, category, remediation);

        var count = globalAdmins.Count;
        var pass = count is >= 2 and <= 4;
        var names = string.Join(", ", globalAdmins.Select(a => a.DisplayName ?? a.UserPrincipalName));
        var currentValue = count == 0 ? "0 (none found — unexpected)" : $"{count}: {names}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckGlobalAdminsHaveMfa(
        List<GraphUserDto>? globalAdmins,
        IReadOnlyDictionary<string, bool?> mfaRegisteredByGraphUserId)
    {
        const string id = "identity-global-admins-mfa";
        const string category = "Identity";
        const string title = "Every Global Administrator has MFA registered";
        const string remediation = "Register MFA for the flagged admin account(s) — a Global Administrator without MFA is the single highest-value target in the tenant for credential-based attacks.";
        if (globalAdmins is null || globalAdmins.Count == 0) return Info(id, title, category, remediation);

        var withoutMfa = globalAdmins
            .Where(a => mfaRegisteredByGraphUserId.GetValueOrDefault(a.Id) != true)
            .Select(a => a.DisplayName ?? a.UserPrincipalName)
            .ToList();

        var pass = withoutMfa.Count == 0;
        return Result(
            id, title, category, pass,
            pass ? $"All {globalAdmins.Count} registered" : $"Missing MFA: {string.Join(", ", withoutMfa)}",
            remediation);
    }

    private static OrcaCheckResultDto CheckMfaEnforced(bool? securityDefaultsEnabled, List<GraphConditionalAccessPolicyDto>? conditionalAccessPolicies)
    {
        const string id = "identity-mfa-enforced";
        const string category = "Identity";
        const string title = "Security Defaults or Conditional Access enforces MFA";
        const string remediation = "Enable Security Defaults, or create an enabled Conditional Access policy that requires MFA, so multi-factor authentication is actually enforced tenant-wide rather than left to individual users to opt into.";
        if (securityDefaultsEnabled is null && conditionalAccessPolicies is null) return Info(id, title, category, remediation);

        var hasEnabledMfaCaPolicy = conditionalAccessPolicies?.Any(p =>
            p.State == "enabled" && (p.BuiltInControls?.Contains("mfa") ?? false)) ?? false;
        var pass = securityDefaultsEnabled == true || hasEnabledMfaCaPolicy;
        var currentValue = securityDefaultsEnabled == true
            ? "Security Defaults enabled"
            : hasEnabledMfaCaPolicy
                ? "Enforced via Conditional Access"
                : "Neither Security Defaults nor an enabled Conditional Access MFA policy found";
        return Result(id, title, category, pass, currentValue, remediation);
    }
}
