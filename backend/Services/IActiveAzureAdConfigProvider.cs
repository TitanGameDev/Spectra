namespace Spectra.Api.Services;

// In-memory cache of the currently-active Azure AD config, read by the four
// client-credential call sites (GraphAppClient, AzureResourceClient,
// ExoPowerShellClient, SccPowerShellClient) and the consent-url endpoint.
// Registered as a Singleton — ExoPowerShellClient/SccPowerShellClient are
// themselves AddSingleton and can't hold a scoped SpectraDbContext directly,
// so rather than mixing "some call sites read the DB, some read a cache
// depending on their own lifetime," all five read from here uniformly.
//
// Unlike IActiveDatabaseProvider this isn't itself file-persisted — the
// AzureAdConfig database row is the durable copy; this is purely a
// request-time cache, seeded at startup and updated by the setup/settings
// save endpoints right after they save. This preserves the same
// "fresh, no restart needed" property the four call sites already had when
// they read straight from IConfiguration, just backed by the DB instead of a
// static config snapshot.
public interface IActiveAzureAdConfigProvider
{
    bool IsConfigured { get; }
    string? TenantId { get; }
    string? FrontendClientId { get; }
    string? BackendClientId { get; }
    string? BackendClientSecret { get; }
    string? ApiScope { get; }

    void Update(string tenantId, string frontendClientId, string backendClientId, string backendClientSecret, string apiScope);
}

public class ActiveAzureAdConfigProvider : IActiveAzureAdConfigProvider
{
    private readonly object _lock = new();
    private bool _isConfigured;
    private string? _tenantId;
    private string? _frontendClientId;
    private string? _backendClientId;
    private string? _backendClientSecret;
    private string? _apiScope;

    public bool IsConfigured { get { lock (_lock) return _isConfigured; } }
    public string? TenantId { get { lock (_lock) return _tenantId; } }
    public string? FrontendClientId { get { lock (_lock) return _frontendClientId; } }
    public string? BackendClientId { get { lock (_lock) return _backendClientId; } }
    public string? BackendClientSecret { get { lock (_lock) return _backendClientSecret; } }
    public string? ApiScope { get { lock (_lock) return _apiScope; } }

    public void Update(string tenantId, string frontendClientId, string backendClientId, string backendClientSecret, string apiScope)
    {
        lock (_lock)
        {
            _tenantId = tenantId;
            _frontendClientId = frontendClientId;
            _backendClientId = backendClientId;
            _backendClientSecret = backendClientSecret;
            _apiScope = apiScope;
            _isConfigured = true;
        }
    }
}
