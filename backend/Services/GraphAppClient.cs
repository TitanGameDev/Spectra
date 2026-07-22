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

public record GraphMfaDto(bool IsMfaRegistered, bool IsMfaCapable, List<string> Methods);

// The List<string> properties are declared nullable even though
// GetConditionalAccessPoliciesAsync below always populates them (via
// GetStringArray, which never returns null) — this type is also what gets
// deserialized straight out of storage, and a customer whose data was last
// collected before these fields existed has stored JSON missing them
// entirely, which System.Text.Json fills in as null rather than []. See the
// normalization in the /api/customers/{id}/security handler in Program.cs.
public record GraphConditionalAccessPolicyDto(
    string DisplayName,
    string State,
    DateTimeOffset? CreatedDateTime,
    DateTimeOffset? ModifiedDateTime,
    List<string>? IncludeUsers,
    List<string>? ExcludeUsers,
    List<string>? IncludeGroups,
    List<string>? ExcludeGroups,
    List<string>? IncludeRoles,
    List<string>? ExcludeRoles,
    List<string>? IncludeApplications,
    List<string>? ExcludeApplications,
    List<string>? ClientAppTypes,
    string? GrantControlsOperator,
    List<string>? BuiltInControls);

public record GraphSecureScoreDto(double CurrentScore, double MaxScore, DateTimeOffset? CreatedDateTime);

public record GraphForwardingRuleDto(string Name, bool Enabled, List<string> ForwardsTo);

public record GraphInboxRuleDto(
    string Name,
    bool Enabled,
    int Sequence,
    List<string> ConditionTypes,
    List<string> ActionTypes,
    List<string> ForwardsTo);

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
        // getMailboxUsageDetail (and the classic Office 365 usage-report family
        // generally) doesn't support $format=application/json — Graph returns a
        // plain 400 "{"Message":"JSON format is not supported."}" if you ask for
        // it. CSV is the only format these actually return, so that's what gets
        // parsed here instead.
        var usage = new Dictionary<string, GraphMailboxUsageDto>(StringComparer.OrdinalIgnoreCase);
        const string url = "https://graph.microsoft.com/v1.0/reports/getMailboxUsageDetail(period='D7')";

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

        var rows = ParseCsv(responseBody);
        if (rows.Count == 0)
        {
            return usage;
        }

        var header = rows[0];
        var upnIdx = header.IndexOf("User Principal Name");
        var storageIdx = header.IndexOf("Storage Used (Byte)");
        var itemCountIdx = header.IndexOf("Item Count");
        var archiveIdx = header.IndexOf("Has Archive");
        if (upnIdx < 0)
        {
            // Unexpected schema (Microsoft has changed these column sets before) —
            // fail soft with no data rather than throw on something cosmetic.
            return usage;
        }

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count <= upnIdx || string.IsNullOrEmpty(row[upnIdx]))
            {
                continue;
            }

            long? sizeBytes = storageIdx >= 0 && storageIdx < row.Count && long.TryParse(row[storageIdx], out var sz) ? sz : null;
            int? itemCount = itemCountIdx >= 0 && itemCountIdx < row.Count && int.TryParse(row[itemCountIdx], out var ic) ? ic : null;
            bool? hasArchive = archiveIdx >= 0 && archiveIdx < row.Count && bool.TryParse(row[archiveIdx], out var ha) ? ha : null;

            usage[row[upnIdx]] = new GraphMailboxUsageDto(sizeBytes, itemCount, hasArchive);
        }

        return usage;
    }

    // Minimal RFC4180-ish CSV parser — handles quoted fields, embedded commas,
    // and escaped ("") quotes, which is all Microsoft's usage-report CSVs need.
    private static List<List<string>> ParseCsv(string csvText)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csvText.Length; i++)
        {
            var c = csvText[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < csvText.Length && csvText[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    // @odata.type -> friendly label for /authenticationMethods entries.
    // Password is deliberately not mapped here — GetAuthenticationMethodsAsync
    // excludes it entirely, since it's not a second factor.
    private static readonly Dictionary<string, string> AuthMethodTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#microsoft.graph.microsoftAuthenticatorAuthenticationMethod"] = "Microsoft Authenticator",
        ["#microsoft.graph.phoneAuthenticationMethod"] = "Phone (SMS/Call)",
        ["#microsoft.graph.fido2AuthenticationMethod"] = "FIDO2 Security Key",
        ["#microsoft.graph.windowsHelloForBusinessAuthenticationMethod"] = "Windows Hello for Business",
        ["#microsoft.graph.softwareOathAuthenticationMethod"] = "Software OATH Token",
        ["#microsoft.graph.emailAuthenticationMethod"] = "Email OTP",
        ["#microsoft.graph.temporaryAccessPassAuthenticationMethod"] = "Temporary Access Pass",
        ["#microsoft.graph.platformCredentialAuthenticationMethod"] = "Platform Credential",
    };

    // One call per user — needs UserAuthenticationMethod.Read.All. This is the
    // live per-user registered-methods list (what each user actually has set
    // up), not the Reports API's tenant-wide registration summary — that
    // endpoint needs a different, easy-to-miss permission (AuditLog.Read.All)
    // and turned out less reliable in practice than just asking per user here.
    public async Task<GraphMfaDto> GetAuthenticationMethodsAsync(string tenantId, string userGraphId, string token, CancellationToken ct = default)
    {
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userGraphId)}/authentication/methods";
        using var doc = await GetGraphJsonAsync(url, token, "Graph authentication methods request", ct);

        var methods = new List<string>();
        if (doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var type = GetOptionalString(item, "@odata.type");
                if (type is null || type.Equals("#microsoft.graph.passwordAuthenticationMethod", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                methods.Add(AuthMethodTypeNames.TryGetValue(type, out var friendly)
                    ? friendly
                    : type.Replace("#microsoft.graph.", "").Replace("AuthenticationMethod", ""));
            }
        }

        return new GraphMfaDto(methods.Count > 0, methods.Count > 0, methods);
    }

    // Tenant-wide, not per-user — needs Policy.Read.All.
    public async Task<List<GraphConditionalAccessPolicyDto>> GetConditionalAccessPoliciesAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var policies = new List<GraphConditionalAccessPolicyDto>();
        using var doc = await GetGraphJsonAsync(
            "https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies", token, "Graph Conditional Access policies request", ct);

        if (doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var displayName = GetOptionalString(item, "displayName");
                var state = GetOptionalString(item, "state");
                if (displayName is null || state is null)
                {
                    continue;
                }

                var users = item.TryGetProperty("conditions", out var conditions) && conditions.TryGetProperty("users", out var usersEl)
                    ? usersEl
                    : default;
                var applications = conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty("applications", out var appsEl)
                    ? appsEl
                    : default;
                var grantControls = item.TryGetProperty("grantControls", out var grantEl) ? grantEl : default;

                policies.Add(new GraphConditionalAccessPolicyDto(
                    displayName,
                    state,
                    GetOptionalDateTimeOffset(item, "createdDateTime"),
                    GetOptionalDateTimeOffset(item, "modifiedDateTime"),
                    GetStringArray(users, "includeUsers"),
                    GetStringArray(users, "excludeUsers"),
                    GetStringArray(users, "includeGroups"),
                    GetStringArray(users, "excludeGroups"),
                    GetStringArray(users, "includeRoles"),
                    GetStringArray(users, "excludeRoles"),
                    GetStringArray(applications, "includeApplications"),
                    GetStringArray(applications, "excludeApplications"),
                    conditions.ValueKind == JsonValueKind.Object ? GetStringArray(conditions, "clientAppTypes") : [],
                    grantControls.ValueKind == JsonValueKind.Object ? GetOptionalString(grantControls, "operator") : null,
                    grantControls.ValueKind == JsonValueKind.Object ? GetStringArray(grantControls, "builtInControls") : []));
            }
        }

        return policies;
    }

    private static List<string> GetStringArray(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    // Tenant-wide, not per-user — needs SecurityEvents.Read.All. Most recent
    // snapshot only; Graph returns these newest-first by default.
    public async Task<GraphSecureScoreDto?> GetSecureScoreAsync(string tenantId, string token, CancellationToken ct = default)
    {
        using var doc = await GetGraphJsonAsync(
            "https://graph.microsoft.com/v1.0/security/secureScores?$top=1", token, "Graph Secure Score request", ct);

        if (!doc.RootElement.TryGetProperty("value", out var valueArray) || valueArray.GetArrayLength() == 0)
        {
            return null;
        }

        var item = valueArray[0];
        double? currentScore = item.TryGetProperty("currentScore", out var cur) && cur.ValueKind == JsonValueKind.Number ? cur.GetDouble() : null;
        double? maxScore = item.TryGetProperty("maxScore", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetDouble() : null;
        if (currentScore is null || maxScore is null)
        {
            return null;
        }

        return new GraphSecureScoreDto(currentScore.Value, maxScore.Value, GetOptionalDateTimeOffset(item, "createdDateTime"));
    }

    // One call per user — needs MailboxSettings.Read. Returns every inbox
    // rule, not just forwarding ones — ForwardsTo is populated where
    // applicable so callers wanting only the security-relevant subset (see
    // Program.cs) can filter on that without a second Graph call.
    // ConditionTypes/ActionTypes are just the JSON property names Graph used
    // (e.g. "subjectContains", "moveToFolder") rather than a fully-parsed
    // human-readable sentence — enough to show what a rule does without
    // building a parser for every one of Graph's condition/action shapes.
    public async Task<List<GraphInboxRuleDto>> GetInboxRulesAsync(string tenantId, string userGraphId, string token, CancellationToken ct = default)
    {
        var rules = new List<GraphInboxRuleDto>();
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userGraphId)}/mailFolders/inbox/messageRules";
        using var doc = await GetGraphJsonAsync(url, token, "Graph inbox message rules request", ct);
        Console.WriteLine($"DEBUG inbox rules for {userGraphId}: {doc.RootElement.GetRawText()}");

        if (doc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var name = GetOptionalString(item, "displayName") ?? "(unnamed rule)";
                var enabled = item.TryGetProperty("isEnabled", out var en) && en.ValueKind == JsonValueKind.True;
                var sequence = item.TryGetProperty("sequence", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number
                    ? seqEl.GetInt32()
                    : 0;

                var conditionTypes = item.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Object
                    ? conditions.EnumerateObject().Select(p => p.Name).ToList()
                    : [];

                var actionTypes = new List<string>();
                var forwardsTo = new List<string>();
                if (item.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Object)
                {
                    actionTypes = actions.EnumerateObject().Select(p => p.Name).ToList();
                    foreach (var actionName in new[] { "forwardTo", "forwardAsAttachmentTo", "redirectTo" })
                    {
                        if (actions.TryGetProperty(actionName, out var recipientsEl) && recipientsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var recipient in recipientsEl.EnumerateArray())
                            {
                                if (recipient.TryGetProperty("emailAddress", out var emailAddress)
                                    && GetOptionalString(emailAddress, "address") is { } address)
                                {
                                    forwardsTo.Add(address);
                                }
                            }
                        }
                    }
                }

                rules.Add(new GraphInboxRuleDto(name, enabled, sequence, conditionTypes, actionTypes, forwardsTo));
            }
        }

        return rules;
    }

    private async Task<JsonDocument> GetGraphJsonAsync(string url, string token, string operationLabel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(responseBody);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new GraphPermissionDeniedException($"{operationLabel} failed: {summary}");
            }
            throw new InvalidOperationException($"{operationLabel} failed: {summary}");
        }

        return JsonDocument.Parse(responseBody);
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
