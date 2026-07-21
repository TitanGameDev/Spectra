using System.Net.Http.Headers;
using System.Text.Json;

namespace Spectra.Api.Services;

public record GraphUserDto(
    string Id,
    string? DisplayName,
    string? Mail,
    string UserPrincipalName,
    string? JobTitle,
    string? Department,
    string? OfficeLocation,
    bool AccountEnabled,
    DateTimeOffset? CreatedDateTime);

public record GraphLicenseDto(string SkuId, string SkuPartNumber);

public record GraphMailboxUsageDto(long? SizeBytes, int? ItemCount, bool? HasArchive);

// Thrown specifically for a 403 from Graph — distinguishes "the Reports.Read.All
// permission genuinely isn't granted yet" from other failures (bad tenant state,
// reporting not yet available, etc.) that look similar but need a different fix.
public class GraphPermissionDeniedException(string message) : Exception(message);

// Calls Microsoft Graph as Spectra's own app registration (client-credentials
// / application-only flow), scoped to a specific customer tenant, rather than
// on behalf of whoever's signed in. This is what lets every Spectra user see
// a customer's data without each of them needing their own Graph permissions
// against that customer's tenant — but it does mean the customer's Entra
// admin must have granted admin consent to Spectra's app registration first
// (see the consent-url endpoint in Program.cs).
public class GraphAppClient(HttpClient httpClient, IConfiguration configuration)
{
    // Public so callers doing many per-user calls (e.g. license details across
    // a whole tenant) can acquire the app token once instead of once per call.
    public async Task<string> GetAppTokenAsync(string tenantId, CancellationToken ct = default)
    {
        var clientId = configuration["AzureAd:ClientId"];
        var clientSecret = configuration["AzureAd:ClientSecret"];
        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException(
                "AzureAd:ClientSecret is not configured — required for per-customer Graph access. See README.");
        }

        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });

        using var response = await httpClient.PostAsync(tokenEndpoint, body, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Couldn't get a token for tenant {tenantId}: {Summarize(responseBody)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task<List<GraphUserDto>> ListUsersAsync(string tenantId, CancellationToken ct = default)
    {
        var token = await GetAppTokenAsync(tenantId, ct);

        var users = new List<GraphUserDto>();
        string? url =
            "https://graph.microsoft.com/v1.0/users?$select=id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation,accountEnabled,createdDateTime&$top=999";

        while (url is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Graph /users request failed: {Summarize(responseBody)}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    users.Add(new GraphUserDto(
                        item.GetProperty("id").GetString()!,
                        GetOptionalString(item, "displayName"),
                        GetOptionalString(item, "mail"),
                        item.GetProperty("userPrincipalName").GetString()!,
                        GetOptionalString(item, "jobTitle"),
                        GetOptionalString(item, "department"),
                        GetOptionalString(item, "officeLocation"),
                        item.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind == JsonValueKind.True,
                        GetOptionalDateTimeOffset(item, "createdDateTime")));
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return users;
    }

    // One call per user — Graph has no bulk "license details for all users"
    // endpoint. Callers should parallelize with modest concurrency rather than
    // awaiting these one at a time for tenants with many users.
    public async Task<List<GraphLicenseDto>> GetLicenseDetailsAsync(string tenantId, string userGraphId, string token, CancellationToken ct = default)
    {
        var licenses = new List<GraphLicenseDto>();
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userGraphId)}/licenseDetails";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph /licenseDetails request failed: {Summarize(responseBody)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var skuId = GetOptionalString(item, "skuId");
                var skuPartNumber = GetOptionalString(item, "skuPartNumber");
                if (skuId is not null && skuPartNumber is not null)
                {
                    licenses.Add(new GraphLicenseDto(skuId, skuPartNumber));
                }
            }
        }

        return licenses;
    }

    // Mailbox size/item count/archive status aren't exposed under User.Read.All —
    // they come from the Reports API, which needs its own Reports.Read.All
    // application permission (see README). One bulk call covers every mailbox
    // in the tenant, keyed by UPN (lowercased) for joining against users.
    public async Task<Dictionary<string, GraphMailboxUsageDto>> GetMailboxUsageByUpnAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var usage = new Dictionary<string, GraphMailboxUsageDto>(StringComparer.OrdinalIgnoreCase);
        const string url = "https://graph.microsoft.com/v1.0/reports/getMailboxUsageDetail(period='D7')?$format=application/json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(responseBody);
            // Distinguish "permission not granted" (403 / Authorization_RequestDenied)
            // from everything else — Reports endpoints fail for other reasons too
            // (e.g. "UnknownTenantId" for tenants with no Exchange Online mailboxes,
            // very new tenants the reporting pipeline hasn't indexed yet, or trial
            // tenants), and blaming the permission when that's not the real problem
            // just sends people re-consenting for no reason.
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new GraphPermissionDeniedException($"Graph mailbox usage report failed: {summary}");
            }
            throw new InvalidOperationException($"Graph mailbox usage report failed: {summary}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var upn = GetOptionalString(item, "userPrincipalName");
                if (upn is null) continue;

                long? sizeBytes = item.TryGetProperty("storageUsedInBytes", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                    ? sizeEl.GetInt64()
                    : null;
                int? itemCount = item.TryGetProperty("itemCount", out var countEl) && countEl.ValueKind == JsonValueKind.Number
                    ? countEl.GetInt32()
                    : null;
                bool? hasArchive = item.TryGetProperty("hasArchive", out var archiveEl) && archiveEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? archiveEl.GetBoolean()
                    : null;

                usage[upn] = new GraphMailboxUsageDto(sizeBytes, itemCount, hasArchive);
            }
        }

        return usage;
    }

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    // Graph/AAD error bodies are verbose JSON — pull out just the message for
    // the exception text that ends up in LastSyncError.
    private static string Summarize(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error_description", out var errDesc))
            {
                return errDesc.GetString() ?? responseBody;
            }
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? responseBody;
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw body.
        }
        return responseBody;
    }
}
