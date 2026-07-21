using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Spectra.Api.Services;

// ServerVersion is only populated by MySqlConnectionTester — Pomelo's EF Core
// provider needs it up front and re-detecting it on every request would be wasteful,
// so it's captured once here and cached by IActiveDatabaseProvider on cutover.
public record ConnectionTestResult(bool Reachable, bool DatabaseExists, string? Error, string? ServerVersion = null);

// Raw ADO.NET helpers for testing/creating a SQL Server database, independent of
// EF Core — used before we know whether the target database (and therefore an
// EF Core connection to it) even exists yet.
public static class SqlServerConnectionTester
{
    public static string BuildConnectionString(string host, int port, string database, string username, string password) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = username,
            Password = password,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
        }.ConnectionString;

    private static string BuildMasterConnectionString(string host, int port, string username, string password) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = "master",
            UserID = username,
            Password = password,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
        }.ConnectionString;

    public static async Task<ConnectionTestResult> TestAsync(
        string host, int port, string database, string username, string password, ILogger? logger = null)
    {
        try
        {
            await using var connection = new SqlConnection(BuildMasterConnectionString(host, port, username, password));
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT database_id FROM sys.databases WHERE name = @name";
            command.Parameters.AddWithValue("@name", database);
            var result = await command.ExecuteScalarAsync();

            return new ConnectionTestResult(true, result is not null, null);
        }
        catch (Exception ex)
        {
            // Full exception (stack trace, inner exceptions) server-side only — the
            // client-facing message stays terse and never includes credentials.
            logger?.LogWarning(ex, "SQL Server connection attempt to {Host}:{Port} failed", host, port);

            var detail = ex.InnerException is not null
                ? $"{ex.GetType().Name}: {ex.Message} ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})"
                : $"{ex.GetType().Name}: {ex.Message}";
            return new ConnectionTestResult(false, false, detail);
        }
    }

    // Caller must validate `database` against a safe identifier pattern first —
    // DDL identifiers can't be parameterized.
    public static async Task CreateDatabaseAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SqlConnection(BuildMasterConnectionString(host, port, username, password));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{database}]";
        await command.ExecuteNonQueryAsync();
    }
}
