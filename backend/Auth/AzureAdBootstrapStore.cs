using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Spectra.Api.Auth;

public record AzureAdBootstrapValues(string? TenantId, string? BackendClientId);

// AddMicrosoftIdentityWebApi needs a TenantId/ClientId at service-registration
// time, before builder.Build() runs — before any DbContext (and therefore
// AzureAdConfig, which lives in the database) exists at all. Mirrors
// ActiveDatabaseProvider's database-provider.json marker file, just read one
// step earlier in the startup sequence, via a standalone Data Protection
// provider pointed at the same keys/ directory the app's real one uses later
// (same key ring, distinct purpose string) rather than through DI.
//
// Only carries TenantId + BackendClientId — no secret is needed here, since
// this is inbound bearer-token validation, not a client-credentials flow.
// Kept in sync by the save endpoints in Program.cs whenever AzureAdConfig changes.
public static class AzureAdBootstrapStore
{
    private const string FileName = "azuread-bootstrap.json";
    private const string ProtectorPurpose = "Spectra.AzureAdConfig.Bootstrap";

    public static AzureAdBootstrapValues Load(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(path))
        {
            return new AzureAdBootstrapValues(null, null);
        }

        try
        {
            var protector = CreateProtector(contentRootPath);
            var marker = JsonSerializer.Deserialize<MarkerFile>(File.ReadAllText(path));
            if (marker is null)
            {
                return new AzureAdBootstrapValues(null, null);
            }
            return new AzureAdBootstrapValues(marker.TenantId, protector.Unprotect(marker.EncryptedBackendClientId));
        }
        catch
        {
            // Corrupt file / undecryptable payload — fall back to unconfigured rather
            // than fail startup; the setup wizard is still reachable either way.
            return new AzureAdBootstrapValues(null, null);
        }
    }

    public static void Save(string contentRootPath, string tenantId, string backendClientId)
    {
        var protector = CreateProtector(contentRootPath);
        var marker = new MarkerFile(tenantId, protector.Protect(backendClientId));
        File.WriteAllText(Path.Combine(contentRootPath, FileName), JsonSerializer.Serialize(marker));
    }

    private static IDataProtector CreateProtector(string contentRootPath)
    {
        var keysDir = new DirectoryInfo(Path.Combine(contentRootPath, "keys"));
        return DataProtectionProvider.Create(keysDir).CreateProtector(ProtectorPurpose);
    }

    private record MarkerFile(string TenantId, string EncryptedBackendClientId);
}
