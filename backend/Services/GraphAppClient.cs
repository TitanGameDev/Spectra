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
    DateTimeOffset? CreatedDateTime,
    // Raw proxyAddresses entries, e.g. "SMTP:primary@contoso.com" (uppercase
    // prefix = primary), "smtp:alias@contoso.com" (lowercase = secondary
    // alias) — parsed into a friendlier shape in Program.cs, kept raw here
    // since that's exactly what Graph returns and needs no interpretation
    // at this layer.
    List<string> ProxyAddresses);

public record GraphLicenseDto(string SkuId, string SkuPartNumber);

public record GraphMailboxUsageDto(long? SizeBytes, int? ItemCount, bool? HasArchive);

public record GraphOneDriveUsageDto(
    string? SiteUrl,
    string? OwnerDisplayName,
    long? StorageUsedBytes,
    long? StorageAllocatedBytes,
    int? FileCount,
    int? ActiveFileCount,
    DateTimeOffset? LastActivityDate);

public record GraphSharePointSiteDto(
    string? SiteId,
    string SiteUrl,
    string? OwnerDisplayName,
    string? OwnerPrincipalName,
    string? RootWebTemplate,
    long? StorageUsedBytes,
    long? StorageAllocatedBytes,
    int? FileCount,
    int? ActiveFileCount,
    DateTimeOffset? LastActivityDate);

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

public record GraphControlScoreDto(string ControlName, string? ControlCategory, string? Description, double Score);

public record GraphSecureScoreDto(double CurrentScore, double MaxScore, DateTimeOffset? CreatedDateTime, List<GraphControlScoreDto> ControlScores);

public record GraphSecureScoreControlProfileDto(
    string Id,
    string? Title,
    string? ControlCategory,
    double MaxScore,
    string? Rank,
    string? Tier,
    string? ImplementationCost,
    string? UserImpact,
    string? ActionType,
    string? Remediation,
    string? RemediationImpact,
    List<string> Threats,
    bool Deprecated);

public record GraphForwardingRuleDto(string Name, bool Enabled, List<string> ForwardsTo);

// An Entra app registration — the definition of an app, owned by this
// tenant. SoonestCredentialExpiry is the minimum endDateTime across every
// password and certificate credential on the app (whichever comes first),
// null if it has none; a useful at-a-glance flag for a stale/soon-to-break
// integration, same idea as the EXO certificate expiry banner elsewhere.
public record GraphApplicationDto(
    string Id,
    string AppId,
    string? DisplayName,
    string? SignInAudience,
    DateTimeOffset? CreatedDateTime,
    DateTimeOffset? SoonestCredentialExpiry);

// An Entra service principal — the "instance" of an app in this tenant,
// covering both apps registered here (App Registrations) and third-party/
// gallery apps consented into this tenant (Enterprise Applications). The two
// lists overlap for locally-registered apps but are shown as separate
// sections since that's how the Entra portal itself presents them.
public record GraphServicePrincipalDto(
    string Id,
    string AppId,
    string? DisplayName,
    string? ServicePrincipalType,
    DateTimeOffset? CreatedDateTime);

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
public class GraphAppClient(HttpClient httpClient, IActiveAzureAdConfigProvider azureAdConfig)
{
    // Public so callers doing many per-user calls (e.g. license details across
    // a whole tenant) can acquire the app token once instead of once per call.
    public async Task<string> GetAppTokenAsync(string tenantId, CancellationToken ct = default)
    {
        var clientId = azureAdConfig.BackendClientId;
        var clientSecret = azureAdConfig.BackendClientSecret;
        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException(
                "Azure AD isn't configured yet — required for per-customer Graph access. See Settings → Authentication.");
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
            "https://graph.microsoft.com/v1.0/users?$select=id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation,accountEnabled,createdDateTime,proxyAddresses,userType&$top=999";

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
                    // Guests (B2B-invited external accounts) aren't members of
                    // this tenant's own domains — they don't show up in the
                    // Microsoft 365 admin center's user list either, so
                    // Spectra shouldn't track them as if they were.
                    if (string.Equals(GetOptionalString(item, "userType"), "Guest", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    users.Add(new GraphUserDto(
                        item.GetProperty("id").GetString()!,
                        GetOptionalString(item, "displayName"),
                        GetOptionalString(item, "mail"),
                        item.GetProperty("userPrincipalName").GetString()!,
                        GetOptionalString(item, "jobTitle"),
                        GetOptionalString(item, "department"),
                        GetOptionalString(item, "officeLocation"),
                        item.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind == JsonValueKind.True,
                        GetOptionalDateTimeOffset(item, "createdDateTime"),
                        GetStringArray(item, "proxyAddresses")));
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

    // Same "usage report" family, same Reports.Read.All permission, and the
    // same CSV-only limitation as GetMailboxUsageByUpnAsync above — see that
    // method's comment for why CSV is the only format these actually return.
    // Keyed by owner UPN, joined against CustomerUser the same way mailbox
    // usage is.
    public async Task<Dictionary<string, GraphOneDriveUsageDto>> GetOneDriveUsageByUpnAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var usage = new Dictionary<string, GraphOneDriveUsageDto>(StringComparer.OrdinalIgnoreCase);
        const string url = "https://graph.microsoft.com/v1.0/reports/getOneDriveUsageAccountDetail(period='D7')";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(responseBody);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new GraphPermissionDeniedException($"Graph OneDrive usage report failed: {summary}");
            }
            throw new InvalidOperationException($"Graph OneDrive usage report failed: {summary}");
        }

        var rows = ParseCsv(responseBody);
        if (rows.Count == 0)
        {
            return usage;
        }

        var header = rows[0];
        var upnIdx = header.IndexOf("Owner Principal Name");
        var siteUrlIdx = header.IndexOf("Site URL");
        var ownerNameIdx = header.IndexOf("Owner Display Name");
        var storageUsedIdx = header.IndexOf("Storage Used (Byte)");
        var storageAllocatedIdx = header.IndexOf("Storage Allocated (Byte)");
        var fileCountIdx = header.IndexOf("File Count");
        var activeFileCountIdx = header.IndexOf("Active File Count");
        var lastActivityIdx = header.IndexOf("Last Activity Date");
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

            string? siteUrl = siteUrlIdx >= 0 && siteUrlIdx < row.Count && !string.IsNullOrEmpty(row[siteUrlIdx]) ? row[siteUrlIdx] : null;
            string? ownerName = ownerNameIdx >= 0 && ownerNameIdx < row.Count && !string.IsNullOrEmpty(row[ownerNameIdx]) ? row[ownerNameIdx] : null;
            long? storageUsed = storageUsedIdx >= 0 && storageUsedIdx < row.Count && long.TryParse(row[storageUsedIdx], out var su) ? su : null;
            long? storageAllocated = storageAllocatedIdx >= 0 && storageAllocatedIdx < row.Count && long.TryParse(row[storageAllocatedIdx], out var sa) ? sa : null;
            int? fileCount = fileCountIdx >= 0 && fileCountIdx < row.Count && int.TryParse(row[fileCountIdx], out var fc) ? fc : null;
            int? activeFileCount = activeFileCountIdx >= 0 && activeFileCountIdx < row.Count && int.TryParse(row[activeFileCountIdx], out var afc) ? afc : null;
            DateTimeOffset? lastActivity = lastActivityIdx >= 0 && lastActivityIdx < row.Count && DateTimeOffset.TryParse(row[lastActivityIdx], out var la) ? la : null;

            usage[row[upnIdx]] = new GraphOneDriveUsageDto(siteUrl, ownerName, storageUsed, storageAllocated, fileCount, activeFileCount, lastActivity);
        }

        return usage;
    }

    // Same usage-report family/permission again — tenant-wide list rather
    // than a per-user join, so returned as a plain list instead of a
    // UPN-keyed dictionary.
    public async Task<List<GraphSharePointSiteDto>> GetSharePointSiteUsageAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var sites = new List<GraphSharePointSiteDto>();
        const string url = "https://graph.microsoft.com/v1.0/reports/getSharePointSiteUsageDetail(period='D7')";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(responseBody);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new GraphPermissionDeniedException($"Graph SharePoint site usage report failed: {summary}");
            }
            throw new InvalidOperationException($"Graph SharePoint site usage report failed: {summary}");
        }

        var rows = ParseCsv(responseBody);
        if (rows.Count == 0)
        {
            return sites;
        }

        var header = rows[0];
        var siteIdIdx = header.IndexOf("Site Id");
        var siteUrlIdx = header.IndexOf("Site URL");
        var ownerNameIdx = header.IndexOf("Owner Display Name");
        var ownerUpnIdx = header.IndexOf("Owner Principal Name");
        var templateIdx = header.IndexOf("Root Web Template");
        var storageUsedIdx = header.IndexOf("Storage Used (Byte)");
        var storageAllocatedIdx = header.IndexOf("Storage Allocated (Byte)");
        var fileCountIdx = header.IndexOf("File Count");
        var activeFileCountIdx = header.IndexOf("Active File Count");
        var lastActivityIdx = header.IndexOf("Last Activity Date");
        if (siteUrlIdx < 0)
        {
            return sites;
        }

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count <= siteUrlIdx || string.IsNullOrEmpty(row[siteUrlIdx]))
            {
                continue;
            }

            string? siteId = siteIdIdx >= 0 && siteIdIdx < row.Count && !string.IsNullOrEmpty(row[siteIdIdx]) ? row[siteIdIdx] : null;
            string? ownerName = ownerNameIdx >= 0 && ownerNameIdx < row.Count && !string.IsNullOrEmpty(row[ownerNameIdx]) ? row[ownerNameIdx] : null;
            string? ownerUpn = ownerUpnIdx >= 0 && ownerUpnIdx < row.Count && !string.IsNullOrEmpty(row[ownerUpnIdx]) ? row[ownerUpnIdx] : null;
            string? template = templateIdx >= 0 && templateIdx < row.Count && !string.IsNullOrEmpty(row[templateIdx]) ? row[templateIdx] : null;
            long? storageUsed = storageUsedIdx >= 0 && storageUsedIdx < row.Count && long.TryParse(row[storageUsedIdx], out var su) ? su : null;
            long? storageAllocated = storageAllocatedIdx >= 0 && storageAllocatedIdx < row.Count && long.TryParse(row[storageAllocatedIdx], out var sa) ? sa : null;
            int? fileCount = fileCountIdx >= 0 && fileCountIdx < row.Count && int.TryParse(row[fileCountIdx], out var fc) ? fc : null;
            int? activeFileCount = activeFileCountIdx >= 0 && activeFileCountIdx < row.Count && int.TryParse(row[activeFileCountIdx], out var afc) ? afc : null;
            DateTimeOffset? lastActivity = lastActivityIdx >= 0 && lastActivityIdx < row.Count && DateTimeOffset.TryParse(row[lastActivityIdx], out var la) ? la : null;

            sites.Add(new GraphSharePointSiteDto(siteId, row[siteUrlIdx], ownerName, ownerUpn, template, storageUsed, storageAllocated, fileCount, activeFileCount, lastActivity));
        }

        return sites;
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

    // Fixed, Microsoft-documented role-template GUID for the built-in Global
    // Administrator directory role — identical across every Entra tenant,
    // same reasoning as GlobalReaderRoleTemplateId above.
    private const string GlobalAdministratorRoleTemplateId = "62e90394-69f5-4237-9190-012177145e10";

    // Tenant-wide, not per-user — needs RoleManagement.ReadWrite.Directory,
    // the same permission already granted for
    // EnsureGlobalReaderRoleAssignedAsync (that permission's read surface
    // covers directory role membership too, not just role assignment
    // writes), so this needs no new consent on top of what EXO setup
    // already requires. Uses the classic /directoryRoles endpoint (not the
    // newer /roleManagement/directory/roleAssignments one used for
    // self-assignment above) since it's a simpler two-call shape for "list
    // the members of a built-in role" — the role has to be "activated" (at
    // least one member ever assigned) to have a /directoryRoles entry,
    // which is true for Global Administrator in every real tenant.
    public async Task<List<GraphUserDto>> GetGlobalAdministratorsAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var roleUrl = $"https://graph.microsoft.com/v1.0/directoryRoles?$filter=roleTemplateId eq '{GlobalAdministratorRoleTemplateId}'";
        using var roleDoc = await GetGraphJsonAsync(roleUrl, token, "Graph directory roles request", ct);

        if (!roleDoc.RootElement.TryGetProperty("value", out var roles) || roles.GetArrayLength() == 0)
        {
            // No /directoryRoles entry means the role has never been
            // activated in this tenant — vanishingly unlikely for Global
            // Administrator specifically, but fail soft rather than throw.
            return [];
        }

        var roleId = GetOptionalString(roles[0], "id");
        if (roleId is null)
        {
            return [];
        }

        var members = new List<GraphUserDto>();
        var membersUrl = $"https://graph.microsoft.com/v1.0/directoryRoles/{roleId}/members?$select=id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation,accountEnabled,createdDateTime";
        using var membersDoc = await GetGraphJsonAsync(membersUrl, token, "Graph directory role members request", ct);

        if (membersDoc.RootElement.TryGetProperty("value", out var valueArray))
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var id = GetOptionalString(item, "id");
                var upn = GetOptionalString(item, "userPrincipalName");
                // Role members can include service principals (apps), which
                // don't have a userPrincipalName — skip those, this check is
                // about human admin accounts.
                if (id is null || upn is null)
                {
                    continue;
                }
                members.Add(new GraphUserDto(
                    id,
                    GetOptionalString(item, "displayName"),
                    GetOptionalString(item, "mail"),
                    upn,
                    GetOptionalString(item, "jobTitle"),
                    GetOptionalString(item, "department"),
                    GetOptionalString(item, "officeLocation"),
                    item.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind == JsonValueKind.True,
                    GetOptionalDateTimeOffset(item, "createdDateTime"),
                    ProxyAddresses: []));
            }
        }

        return members;
    }

    // Tenant-wide — needs Policy.Read.All, the same permission already used
    // for Conditional Access above. Null (not false) if the policy can't be
    // read, so callers can distinguish "confirmed disabled" from "unknown".
    public async Task<bool?> GetSecurityDefaultsEnabledAsync(string tenantId, string token, CancellationToken ct = default)
    {
        using var doc = await GetGraphJsonAsync(
            "https://graph.microsoft.com/v1.0/policies/identitySecurityDefaultsEnforcementPolicy", token, "Graph Security Defaults policy request", ct);

        return doc.RootElement.TryGetProperty("isEnabled", out var isEnabled) && isEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? isEnabled.GetBoolean()
            : null;
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

        var controlScores = new List<GraphControlScoreDto>();
        if (item.TryGetProperty("controlScores", out var controlScoresEl) && controlScoresEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var control in controlScoresEl.EnumerateArray())
            {
                var controlName = GetOptionalString(control, "controlName");
                if (controlName is null) continue;
                var score = control.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
                    ? scoreEl.GetDouble()
                    : 0;
                controlScores.Add(new GraphControlScoreDto(
                    controlName,
                    GetOptionalString(control, "controlCategory"),
                    GetOptionalString(control, "description"),
                    score));
            }
        }

        return new GraphSecureScoreDto(currentScore.Value, maxScore.Value, GetOptionalDateTimeOffset(item, "createdDateTime"), controlScores);
    }

    // Tenant-wide catalog of every control Secure Score can evaluate — title,
    // remediation guidance, max score, category. This is what turns a bare
    // percentage into an actionable checklist; joined against the achieved
    // controlScores above (by ControlName == Id) at the API layer. Same
    // SecurityEvents.Read.All permission as GetSecureScoreAsync.
    public async Task<List<GraphSecureScoreControlProfileDto>> GetSecureScoreControlProfilesAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var profiles = new List<GraphSecureScoreControlProfileDto>();
        string? url = "https://graph.microsoft.com/v1.0/security/secureScoreControlProfiles?$top=200";

        while (url is not null)
        {
            using var doc = await GetGraphJsonAsync(url, token, "Graph Secure Score control profiles request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray2))
            {
                foreach (var item2 in valueArray2.EnumerateArray())
                {
                    var id = GetOptionalString(item2, "id");
                    if (id is null) continue;

                    var maxScore = item2.TryGetProperty("maxScore", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number
                        ? maxEl.GetDouble()
                        : 0;
                    var deprecated = item2.TryGetProperty("deprecated", out var depEl) && depEl.ValueKind == JsonValueKind.True;

                    profiles.Add(new GraphSecureScoreControlProfileDto(
                        id,
                        GetOptionalString(item2, "title"),
                        GetOptionalString(item2, "controlCategory"),
                        maxScore,
                        GetOptionalString(item2, "rank"),
                        GetOptionalString(item2, "tier"),
                        GetOptionalString(item2, "implementationCost"),
                        GetOptionalString(item2, "userImpact"),
                        GetOptionalString(item2, "actionType"),
                        GetOptionalString(item2, "remediation"),
                        GetOptionalString(item2, "remediationImpact"),
                        GetStringArray(item2, "threats"),
                        deprecated));
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return profiles;
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

    // Built-in Global Reader role-template GUID — fixed and identical across
    // every Entra tenant (unlike custom roles), which is what makes hardcoding
    // it safe. Sufficient for read-only Exchange Online PowerShell app-only
    // cmdlets per Microsoft's documented app-only auth model, so no separate
    // Exchange-RBAC-specific role assignment is needed on top of this.
    private const string GlobalReaderRoleTemplateId = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";

    // Grants Spectra's own service principal read access to run Exchange
    // Online PowerShell cmdlets in the customer's tenant — a separate,
    // independent setup step from Graph admin consent (which only covers
    // Graph API calls, not EXO PowerShell). Idempotent: safe to call on every
    // collection run until it succeeds. Needs RoleManagement.ReadWrite.Directory.
    public async Task<bool> EnsureGlobalReaderRoleAssignedAsync(string tenantId, string token, CancellationToken ct = default)
    {
        // For an app-only (client-credentials) token, the "oid" claim is the
        // calling application's own service principal object id in the
        // resource tenant — exactly what's needed as the role assignment's
        // principalId, without a second Graph call or an extra
        // Application.Read.All permission.
        var principalId = GetTokenClaim(token, "oid");

        var body = JsonSerializer.Serialize(new
        {
            principalId,
            roleDefinitionId = GlobalReaderRoleTemplateId,
            directoryScopeId = "/",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignments")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var summary = Summarize(responseBody);

        // A prior successful call (this run, or an earlier one that didn't
        // get to persist ExoRoleAssigned before a crash/restart) already
        // created the same assignment — Graph reports that as a conflict,
        // not success, so it's treated as success explicitly here too.
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict
            || summary.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new GraphPermissionDeniedException($"Global Reader role assignment failed: {summary}");
        }
        throw new InvalidOperationException($"Global Reader role assignment failed: {summary}");
    }

    // Microsoft conceals real user identities in usage-report data by default,
    // tenant-wide — the fix is normally a manual toggle in the Microsoft 365
    // admin center (Settings -> Org Settings -> Services -> Reports), but it's
    // also a plain Graph setting Spectra can flip itself, same idea as
    // EnsureGlobalReaderRoleAssignedAsync above. Needs ReportSettings.ReadWrite.All.
    // Idempotent — safe to call on every collection run where concealment is
    // detected, until it succeeds.
    public async Task<bool> EnsureReportIdentitiesRevealedAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://graph.microsoft.com/v1.0/admin/reportSettings")
        {
            Content = new StringContent("""{"displayConcealedNames":false}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var summary = Summarize(responseBody);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new GraphPermissionDeniedException($"Report settings update failed: {summary}");
        }
        throw new InvalidOperationException($"Report settings update failed: {summary}");
    }

    // Exchange Online's Connect-ExchangeOnline -Organization parameter needs
    // the tenant's *.onmicrosoft.com domain, not the tenant ID GUID
    // Customer.TenantId stores (that GUID works fine for Graph's own token
    // endpoint and for the role assignment above, but EXO PowerShell's
    // app-only auth is documented against the domain form) — this resolves it.
    public async Task<string?> GetInitialDomainAsync(string tenantId, string token, CancellationToken ct = default)
    {
        using var doc = await GetGraphJsonAsync(
            "https://graph.microsoft.com/v1.0/organization?$select=verifiedDomains", token, "Graph organization request", ct);

        if (!doc.RootElement.TryGetProperty("value", out var valueArray) || valueArray.GetArrayLength() == 0)
        {
            return null;
        }

        if (valueArray[0].TryGetProperty("verifiedDomains", out var domains) && domains.ValueKind == JsonValueKind.Array)
        {
            foreach (var domain in domains.EnumerateArray())
            {
                if (domain.TryGetProperty("isInitial", out var isInitial) && isInitial.ValueKind == JsonValueKind.True)
                {
                    return GetOptionalString(domain, "name");
                }
            }
        }

        return null;
    }

    // Every verified domain on the tenant, for SPF/DMARC DNS checks — the
    // same /organization?$select=verifiedDomains call GetInitialDomainAsync
    // already relies on above, so this needs no permission beyond what's
    // already granted (no new admin consent step for this feature).
    public async Task<List<string>> GetVerifiedDomainsAsync(string tenantId, string token, CancellationToken ct = default)
    {
        using var doc = await GetGraphJsonAsync(
            "https://graph.microsoft.com/v1.0/organization?$select=verifiedDomains", token, "Graph organization request", ct);

        var result = new List<string>();
        if (!doc.RootElement.TryGetProperty("value", out var valueArray) || valueArray.GetArrayLength() == 0)
        {
            return result;
        }

        if (valueArray[0].TryGetProperty("verifiedDomains", out var domains) && domains.ValueKind == JsonValueKind.Array)
        {
            foreach (var domain in domains.EnumerateArray())
            {
                var name = GetOptionalString(domain, "name");
                if (name is not null)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    // App registrations owned by this tenant — needs Application.Read.All
    // (application permission), a new Graph permission on top of everything
    // else this class uses, so existing customers need to re-grant consent
    // (Settings → Customers → Grant consent) before this starts returning data.
    public async Task<List<GraphApplicationDto>> ListApplicationsAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var apps = new List<GraphApplicationDto>();
        string? url = "https://graph.microsoft.com/v1.0/applications?$select=id,appId,displayName,signInAudience,createdDateTime,passwordCredentials,keyCredentials&$top=999";

        while (url is not null)
        {
            using var doc = await GetGraphJsonAsync(url, token, "Graph /applications request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    apps.Add(new GraphApplicationDto(
                        item.GetProperty("id").GetString()!,
                        GetOptionalString(item, "appId") ?? "",
                        GetOptionalString(item, "displayName"),
                        GetOptionalString(item, "signInAudience"),
                        GetOptionalDateTimeOffset(item, "createdDateTime"),
                        GetSoonestCredentialExpiry(item)));
                }
            }
            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return apps;
    }

    // Enterprise Applications — every service principal in this tenant,
    // including third-party/gallery apps consented in by an admin, not just
    // ones registered here. Same Application.Read.All permission as
    // ListApplicationsAsync above.
    public async Task<List<GraphServicePrincipalDto>> ListServicePrincipalsAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var servicePrincipals = new List<GraphServicePrincipalDto>();
        string? url = "https://graph.microsoft.com/v1.0/servicePrincipals?$select=id,appId,displayName,servicePrincipalType,createdDateTime&$top=999";

        while (url is not null)
        {
            using var doc = await GetGraphJsonAsync(url, token, "Graph /servicePrincipals request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    servicePrincipals.Add(new GraphServicePrincipalDto(
                        item.GetProperty("id").GetString()!,
                        GetOptionalString(item, "appId") ?? "",
                        GetOptionalString(item, "displayName"),
                        GetOptionalString(item, "servicePrincipalType"),
                        GetOptionalDateTimeOffset(item, "createdDateTime")));
                }
            }
            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return servicePrincipals;
    }

    private static DateTimeOffset? GetSoonestCredentialExpiry(JsonElement item)
    {
        DateTimeOffset? soonest = null;
        foreach (var arrayName in new[] { "passwordCredentials", "keyCredentials" })
        {
            if (item.TryGetProperty(arrayName, out var creds) && creds.ValueKind == JsonValueKind.Array)
            {
                foreach (var cred in creds.EnumerateArray())
                {
                    var expiry = GetOptionalDateTimeOffset(cred, "endDateTime");
                    if (expiry is not null && (soonest is null || expiry < soonest))
                    {
                        soonest = expiry;
                    }
                }
            }
        }
        return soonest;
    }

    // Decodes a claim straight out of a JWT's payload segment without
    // validating the signature — safe here specifically because the token
    // was just freshly issued to us, in this same process, by Azure AD's own
    // token endpoint (GetAppTokenAsync); this never touches a token that
    // came from anywhere else.
    private static string GetTokenClaim(string jwt, string claimName)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Malformed access token — expected a JWT with a payload segment.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        var bytes = Convert.FromBase64String(payload);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty(claimName).GetString()!;
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
