namespace Spectra.Api.Data;

// Single-row table — the external database connection an admin has configured,
// if any. Lives inside whichever database is currently active (SQLite until
// cutover, then the external database itself afterward) so it always travels
// with the data it describes.
public class DatabaseConnectionConfig
{
    public int Id { get; set; }

    // "sqlserver" or "mysql".
    public required string DatabaseType { get; set; }

    public required string Host { get; set; }
    public int Port { get; set; }
    public required string DatabaseName { get; set; }
    public required string Username { get; set; }

    // Encrypted via ASP.NET Core Data Protection — never stored or returned in plain text.
    public required string EncryptedPassword { get; set; }

    // True once the target database has been confirmed to exist / been created.
    public bool IsProvisioned { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedByEmail { get; set; }
}
