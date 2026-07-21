using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Spectra.Api.Services;

// The single source of truth for which database the app is currently using.
// Registered as a Singleton and read by SpectraDbContext's factory-based
// AddDbContext registration on every scope resolution (~every request), so
// calling SwitchTo takes effect for the very next request — no restart needed.
//
// Persisted to a local file (connection string encrypted via Data Protection)
// so a later restart — for any reason, not just this hot-swap — comes back up
// pointed at the same database rather than reverting to SQLite.
public interface IActiveDatabaseProvider
{
    string Kind { get; } // "sqlite", "sqlserver", or "mysql"
    string ConnectionString { get; }

    // Only meaningful when Kind == "mysql" — Pomelo's EF Core provider needs a
    // ServerVersion up front, and re-detecting it on every request would be
    // wasteful, so it's captured once at cutover time and cached here.
    string? MySqlServerVersion { get; }

    void SwitchTo(string kind, string connectionString, string? mySqlServerVersion = null);
}

public class ActiveDatabaseProvider : IActiveDatabaseProvider
{
    private readonly string _markerPath;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();
    private string _kind;
    private string _connectionString;
    private string? _mySqlServerVersion;

    public ActiveDatabaseProvider(IHostEnvironment env, IDataProtectionProvider dataProtection, IConfiguration configuration)
    {
        _markerPath = Path.Combine(env.ContentRootPath, "database-provider.json");
        _protector = dataProtection.CreateProtector("Spectra.ActiveDatabaseProvider");
        _kind = "sqlite";
        _connectionString = configuration.GetConnectionString("Default") ?? "Data Source=spectra.db";

        Load();
    }

    public string Kind
    {
        get { lock (_lock) return _kind; }
    }

    public string ConnectionString
    {
        get { lock (_lock) return _connectionString; }
    }

    public string? MySqlServerVersion
    {
        get { lock (_lock) return _mySqlServerVersion; }
    }

    public void SwitchTo(string kind, string connectionString, string? mySqlServerVersion = null)
    {
        lock (_lock)
        {
            _kind = kind;
            _connectionString = connectionString;
            _mySqlServerVersion = mySqlServerVersion;
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_markerPath))
        {
            return;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<MarkerFile>(File.ReadAllText(_markerPath));
            if (marker is null)
            {
                return;
            }
            _kind = marker.Kind;
            _connectionString = _protector.Unprotect(marker.EncryptedConnectionString);
            _mySqlServerVersion = marker.MySqlServerVersion;
        }
        catch
        {
            // Corrupt marker file or undecryptable payload (e.g. Data Protection keys
            // changed) — fall back to the default SQLite connection instead of failing startup.
        }
    }

    private void Save()
    {
        var marker = new MarkerFile(_kind, _protector.Protect(_connectionString), _mySqlServerVersion);
        File.WriteAllText(_markerPath, JsonSerializer.Serialize(marker));
    }

    private record MarkerFile(string Kind, string EncryptedConnectionString, string? MySqlServerVersion);
}
