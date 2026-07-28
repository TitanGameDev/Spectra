using System.Net.Http.Headers;
using System.Text.Json;

namespace Spectra.Api.Services;

public record AzureSubscriptionDto(string Id, string DisplayName, string State);

public record AzureVirtualMachineDto(
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string Name,
    string Location,
    string? VmSize,
    string? OsType,
    string? PowerState);

public record AzureAppServiceDto(
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string Name,
    string? Kind,
    string? State,
    string? DefaultHostName,
    string Location);

public record AzureReservationDto(
    string ReservationOrderId,
    string? DisplayName,
    string? SkuName,
    int Quantity,
    string? ProvisioningState,
    DateTimeOffset? ExpiryDateTime,
    string? Term,
    string? AppliedScopeType);

// Thrown specifically for a 403 from Azure Resource Manager — distinguishes
// "the RBAC role genuinely isn't assigned yet" from other failures. Reused
// across both the subscription-scoped Reader role and the tenant-scoped
// Reservations Reader role (two separate grants, see README), same
// one-exception-many-messages convention as GraphPermissionDeniedException.
public class AzureAccessDeniedException(string message) : Exception(message);

// Calls Azure Resource Manager as Spectra's own app registration
// (client-credentials / application-only flow), same app as GraphAppClient
// but a different token audience. Unlike Graph, ARM access is authorized
// purely by Azure RBAC role assignment on a subscription/resource — there's
// no admin-consent screen involved at all, and no new app-registration
// permission needed here. The customer's Azure admin has to separately
// assign a role (e.g. Reader) to Spectra's app on their subscription via
// Access Control (IAM) — see README for the exact steps. That app's service
// principal already exists in their tenant once they've done the ordinary
// Graph admin-consent step elsewhere in this app, so this is purely an
// additional RBAC grant, not a new consent flow.
public class AzureResourceClient(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<string> GetAppTokenAsync(string tenantId, CancellationToken ct = default)
    {
        var clientId = configuration["AzureAd:ClientId"];
        var clientSecret = configuration["AzureAd:ClientSecret"];
        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException(
                "AzureAd:ClientSecret is not configured — required for per-customer Azure Resource Manager access. See README.");
        }

        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://management.azure.com/.default",
            ["grant_type"] = "client_credentials",
        });

        using var response = await httpClient.PostAsync(tokenEndpoint, body, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Couldn't get an Azure Resource Manager token for tenant {tenantId}: {Summarize(responseBody)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    // Only returns subscriptions the app's service principal has an RBAC role
    // assignment on — an empty result here almost always means "Reader hasn't
    // been assigned to any subscription yet", not "this tenant has no subscriptions".
    public async Task<List<AzureSubscriptionDto>> ListSubscriptionsAsync(string token, CancellationToken ct = default)
    {
        var subscriptions = new List<AzureSubscriptionDto>();
        string? url = "https://management.azure.com/subscriptions?api-version=2022-12-01";

        while (url is not null)
        {
            using var doc = await GetArmJsonAsync(url, token, "Azure subscriptions request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    subscriptions.Add(new AzureSubscriptionDto(
                        item.GetProperty("subscriptionId").GetString()!,
                        GetOptionalString(item, "displayName") ?? "(unnamed subscription)",
                        GetOptionalString(item, "state") ?? "Unknown"));
                }
            }
            url = GetNextLink(root);
        }

        return subscriptions;
    }

    public async Task<List<AzureVirtualMachineDto>> ListVirtualMachinesAsync(
        string subscriptionId, string subscriptionName, string token, CancellationToken ct = default)
    {
        var vms = new List<AzureVirtualMachineDto>();
        // statusOnly=true folds each VM's instanceView (power state) into the
        // list response — no separate per-VM round trip needed.
        string? url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/virtualMachines?statusOnly=true&api-version=2024-07-01";

        while (url is not null)
        {
            using var doc = await GetArmJsonAsync(url, token, "Azure virtual machines request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    var properties = item.TryGetProperty("properties", out var p) ? p : default;
                    vms.Add(new AzureVirtualMachineDto(
                        subscriptionId,
                        subscriptionName,
                        GetResourceGroupFromId(id),
                        item.GetProperty("name").GetString()!,
                        GetOptionalString(item, "location") ?? "",
                        GetVmSize(properties),
                        GetOsType(properties),
                        GetPowerState(properties)));
                }
            }
            url = GetNextLink(root);
        }

        return vms;
    }

    public async Task<List<AzureAppServiceDto>> ListAppServicesAsync(
        string subscriptionId, string subscriptionName, string token, CancellationToken ct = default)
    {
        var apps = new List<AzureAppServiceDto>();
        string? url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Web/sites?api-version=2023-12-01";

        while (url is not null)
        {
            using var doc = await GetArmJsonAsync(url, token, "Azure App Services request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    var properties = item.TryGetProperty("properties", out var p) ? p : default;
                    apps.Add(new AzureAppServiceDto(
                        subscriptionId,
                        subscriptionName,
                        GetResourceGroupFromId(id),
                        item.GetProperty("name").GetString()!,
                        GetOptionalString(item, "kind"),
                        GetOptionalString(properties, "state"),
                        GetOptionalString(properties, "defaultHostName"),
                        GetOptionalString(item, "location") ?? ""));
                }
            }
            url = GetNextLink(root);
        }

        return apps;
    }

    // Reservations are a tenant-level resource, not a subscription-scoped
    // one — one call at the tenant root lists every order, then one call per
    // order for its individual reservations (sku, quantity, applied scope).
    // Needs the Reservations Reader role at tenant scope (Azure Portal →
    // Home → Reservations → Role assignment) — a separate, rarer grant from
    // the subscription Reader role above, assigned on a completely different
    // screen. See README for the exact steps.
    public async Task<List<AzureReservationDto>> ListReservationsAsync(string token, CancellationToken ct = default)
    {
        var reservations = new List<AzureReservationDto>();
        string? ordersUrl = "https://management.azure.com/providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01";

        while (ordersUrl is not null)
        {
            using var doc = await GetArmJsonAsync(ordersUrl, token, "Azure reservation orders request", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var orders))
            {
                foreach (var order in orders.EnumerateArray())
                {
                    var orderId = order.GetProperty("name").GetString()!;
                    var orderProps = order.TryGetProperty("properties", out var op) ? op : default;
                    var term = GetOptionalString(orderProps, "term");

                    string? reservationsUrl = $"https://management.azure.com/providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations?api-version=2022-11-01";
                    while (reservationsUrl is not null)
                    {
                        using var reservationDoc = await GetArmJsonAsync(reservationsUrl, token, "Azure reservations request", ct);
                        var reservationRoot = reservationDoc.RootElement;
                        if (reservationRoot.TryGetProperty("value", out var reservationValues))
                        {
                            foreach (var reservation in reservationValues.EnumerateArray())
                            {
                                var rProps = reservation.TryGetProperty("properties", out var rp) ? rp : default;
                                reservations.Add(new AzureReservationDto(
                                    orderId,
                                    GetOptionalString(rProps, "displayName"),
                                    reservation.TryGetProperty("sku", out var sku) ? GetOptionalString(sku, "name") : null,
                                    rProps.ValueKind == JsonValueKind.Object && rProps.TryGetProperty("quantity", out var qty) && qty.ValueKind == JsonValueKind.Number
                                        ? qty.GetInt32()
                                        : 0,
                                    GetOptionalString(rProps, "provisioningState"),
                                    GetOptionalDateTimeOffset(rProps, "expiryDateTime"),
                                    term,
                                    GetOptionalString(rProps, "appliedScopeType")));
                            }
                        }
                        reservationsUrl = GetNextLink(reservationRoot);
                    }
                }
            }
            ordersUrl = GetNextLink(root);
        }

        return reservations;
    }

    private static string GetResourceGroupFromId(string resourceId)
    {
        // .../resourceGroups/{name}/providers/... — every ARM resource ID has
        // this segment regardless of resource type.
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, p => p.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "";
    }

    private static string? GetVmSize(JsonElement properties) =>
        properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("hardwareProfile", out var hw)
            ? GetOptionalString(hw, "vmSize")
            : null;

    private static string? GetOsType(JsonElement properties) =>
        properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("storageProfile", out var sp)
        && sp.TryGetProperty("osDisk", out var osDisk)
            ? GetOptionalString(osDisk, "osType")
            : null;

    // Only present when the list call includes statusOnly=true — the
    // "PowerState/..." status code is the one that actually matters to a
    // reader; the other status codes ARM returns here (ProvisioningState,
    // etc.) are noise for this purpose.
    private static string? GetPowerState(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("instanceView", out var instanceView)
            || !instanceView.TryGetProperty("statuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var status in statuses.EnumerateArray())
        {
            var code = GetOptionalString(status, "code");
            if (code is not null && code.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))
            {
                return code["PowerState/".Length..];
            }
        }
        return null;
    }

    private static string? GetNextLink(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;

    private async Task<JsonDocument> GetArmJsonAsync(string url, string token, string operationLabel, CancellationToken ct)
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
                throw new AzureAccessDeniedException($"{operationLabel} failed: {summary}");
            }
            throw new InvalidOperationException($"{operationLabel} failed: {summary}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    // ARM error bodies follow the same {"error":{"message":...}} shape Graph
    // uses, just without the OAuth-specific error_description variant.
    private static string Summarize(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
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
