namespace Spectra.Api.Services;

// Evaluates SPF/DMARC DNS checks against a customer's verified domains —
// Spectra's take on ORCA's "Domains" category, but sourced from live public
// DNS lookups (see DnsCheckClient) rather than Graph or Exchange Online
// PowerShell. Needs no admin consent or setup at all — just the verified
// domain list (already fetched via GraphAppClient.GetVerifiedDomainsAsync)
// and outbound DNS access from wherever the backend runs.
public static class DnsCheckEvaluator
{
    public static List<OrcaCheckResultDto> Evaluate(List<DnsRecordCheckDto>? domainChecks)
    {
        return
        [
            CheckSpfExists(domainChecks),
            CheckSpfHardFail(domainChecks),
            CheckDmarcExists(domainChecks),
            CheckDmarcEnforced(domainChecks),
        ];
    }

    private static OrcaCheckResultDto Info(string id, string title, string category, string remediation) =>
        new(id, title, category, "info", "Not collected", remediation);

    private static OrcaCheckResultDto Result(string id, string title, string category, bool pass, string currentValue, string remediation) =>
        new(id, title, category, pass ? "pass" : "fail", currentValue, remediation);

    private static OrcaCheckResultDto CheckSpfExists(List<DnsRecordCheckDto>? domainChecks)
    {
        const string id = "dns-spf-exists";
        const string category = "Domains";
        const string title = "SPF record exists for every domain";
        const string remediation = "Publish an SPF TXT record for the flagged domain(s) — without one, receiving mail servers have no authoritative list of which servers are allowed to send mail as your domain, making sender spoofing trivial.";
        if (domainChecks is null) return Info(id, title, category, remediation);

        var missing = domainChecks.Where(d => !d.SpfFound).Select(d => d.Domain).ToList();
        var pass = missing.Count == 0;
        var currentValue = pass ? $"All {domainChecks.Count} domain(s) have SPF" : $"Missing: {string.Join(", ", missing)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckSpfHardFail(List<DnsRecordCheckDto>? domainChecks)
    {
        const string id = "dns-spf-hardfail";
        const string category = "Domains";
        const string title = "SPF record ends in a hard fail (-all)";
        const string remediation = "Change the flagged domain(s)' SPF record to end in \"-all\" rather than \"~all\"/\"+all\"/\"?all\" — anything short of a hard fail lets mail from unlisted servers through (or merely flags it), rather than actually rejecting it.";
        var withSpf = domainChecks?.Where(d => d.SpfRecord is not null).ToList();
        if (withSpf is null || withSpf.Count == 0) return Info(id, title, category, remediation);

        var weak = withSpf.Where(d => !d.SpfRecord!.TrimEnd().EndsWith("-all", StringComparison.OrdinalIgnoreCase)).Select(d => d.Domain).ToList();
        var pass = weak.Count == 0;
        var currentValue = pass ? $"All {withSpf.Count} domain(s) hard-fail" : $"Not hard-fail: {string.Join(", ", weak)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckDmarcExists(List<DnsRecordCheckDto>? domainChecks)
    {
        const string id = "dns-dmarc-exists";
        const string category = "Domains";
        const string title = "DMARC record exists for every domain";
        const string remediation = "Publish a DMARC TXT record at _dmarc.<domain> for the flagged domain(s) — without one, there's no policy telling receiving mail servers what to do with mail that fails SPF/DKIM, and no reporting visibility into spoofing attempts.";
        if (domainChecks is null) return Info(id, title, category, remediation);

        var missing = domainChecks.Where(d => !d.DmarcFound).Select(d => d.Domain).ToList();
        var pass = missing.Count == 0;
        var currentValue = pass ? $"All {domainChecks.Count} domain(s) have DMARC" : $"Missing: {string.Join(", ", missing)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }

    private static OrcaCheckResultDto CheckDmarcEnforced(List<DnsRecordCheckDto>? domainChecks)
    {
        const string id = "dns-dmarc-enforced";
        const string category = "Domains";
        const string title = "DMARC policy is enforced (quarantine or reject)";
        const string remediation = "Move the flagged domain(s)' DMARC policy from \"p=none\" to \"p=quarantine\" or \"p=reject\" once reports confirm legitimate mail passes — \"p=none\" only monitors and reports, it doesn't actually stop spoofed mail from reaching inboxes.";
        var withDmarc = domainChecks?.Where(d => d.DmarcPolicy is not null).ToList();
        if (withDmarc is null || withDmarc.Count == 0) return Info(id, title, category, remediation);

        var notEnforced = withDmarc
            .Where(d => d.DmarcPolicy is null || (!d.DmarcPolicy.Equals("quarantine", StringComparison.OrdinalIgnoreCase) && !d.DmarcPolicy.Equals("reject", StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Domain)
            .ToList();
        var pass = notEnforced.Count == 0;
        var currentValue = pass ? $"All {withDmarc.Count} domain(s) enforced" : $"Not enforced (p=none): {string.Join(", ", notEnforced)}";
        return Result(id, title, category, pass, currentValue, remediation);
    }
}
