namespace Spectra.Api.Services;

// Tracks whether the currently active database (SQLite, SQL Server, or MySQL)
// is actually reachable. Set at startup and re-checked on every authenticated
// request (see SpectraClaimsTransformation) so a database that goes bad after
// startup — or comes back — is reflected without a restart. Read by
// /api/system/status to drive the frontend's database-setup screen.
public class DatabaseHealth
{
    private readonly object _lock = new();
    private bool _isHealthy = true;
    private string? _error;

    public bool IsHealthy { get { lock (_lock) return _isHealthy; } }
    public string? Error { get { lock (_lock) return _error; } }

    public void MarkHealthy()
    {
        lock (_lock)
        {
            _isHealthy = true;
            _error = null;
        }
    }

    public void MarkUnhealthy(string error)
    {
        lock (_lock)
        {
            _isHealthy = false;
            _error = error;
        }
    }
}
