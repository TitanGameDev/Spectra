using DnsClient;

namespace Spectra.Api.Services;

public record DnsRecordCheckDto(
    string Domain,
    bool SpfFound,
    string? SpfRecord,
    bool DmarcFound,
    string? DmarcRecord,
    string? DmarcPolicy);

// Pure DNS TXT-record lookups for SPF/DMARC — unlike the EXO and Graph
// checks, this needs no auth and no admin consent, just outbound DNS access
// from wherever this process runs. Uses DnsClient (a pure-.NET library)
// rather than shelling out to `dig`/`nslookup`, since .NET's built-in
// System.Net.Dns has no TXT record support and there's no need to depend on
// an external binary when a library exists.
public class DnsCheckClient
{
    private readonly LookupClient _lookup = new();

    public async Task<DnsRecordCheckDto> CheckDomainAsync(string domain, CancellationToken ct = default)
    {
        var spfRecords = await QueryTxtAsync(domain, ct);
        var spfRecord = spfRecords.FirstOrDefault(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));

        var dmarcRecords = await QueryTxtAsync($"_dmarc.{domain}", ct);
        var dmarcRecord = dmarcRecords.FirstOrDefault(r => r.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        var dmarcPolicy = dmarcRecord is null ? null : ExtractDmarcTag(dmarcRecord, "p");

        return new DnsRecordCheckDto(
            domain,
            SpfFound: spfRecord is not null,
            SpfRecord: spfRecord,
            DmarcFound: dmarcRecord is not null,
            DmarcRecord: dmarcRecord,
            DmarcPolicy: dmarcPolicy);
    }

    private async Task<List<string>> QueryTxtAsync(string name, CancellationToken ct)
    {
        try
        {
            var result = await _lookup.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            return result.Answers.TxtRecords()
                .SelectMany(r => r.Text)
                .ToList();
        }
        catch (DnsResponseException)
        {
            // NXDOMAIN / no records — a domain with no SPF or DMARC record
            // at all is a normal, expected outcome here, not a failure.
            return [];
        }
    }

    private static string? ExtractDmarcTag(string record, string tag)
    {
        foreach (var part in record.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].Trim();
            }
        }
        return null;
    }
}
