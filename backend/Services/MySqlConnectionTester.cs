using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Spectra.Api.Services;

// Mirrors SqlServerConnectionTester but for MySQL — genuinely different wire
// protocol, so it needs its own connector (MySqlConnector) rather than sharing
// any code with the SQL Server path.
public static class MySqlConnectionTester
{
    public static string BuildConnectionString(string host, int port, string database, string username, string password) =>
        new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            Database = database,
            UserID = username,
            Password = password,
            ConnectionTimeout = 8,
        }.ConnectionString;

    // No Database set — used to reach the server before we know the target
    // database exists (and to run CREATE DATABASE, which can't target itself).
    private static string BuildServerConnectionString(string host, int port, string username, string password) =>
        new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            UserID = username,
            Password = password,
            ConnectionTimeout = 8,
        }.ConnectionString;

    public static async Task<ConnectionTestResult> TestAsync(
        string host, int port, string database, string username, string password, ILogger? logger = null)
    {
        try
        {
            await using var connection = new MySqlConnection(BuildServerConnectionString(host, port, username, password));
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @name";
            command.Parameters.AddWithValue("@name", database);
            var result = await command.ExecuteScalarAsync();

            return new ConnectionTestResult(true, result is not null, null, connection.ServerVersion);
        }
        catch (Exception ex)
        {
            // Full exception (stack trace, inner exceptions) server-side only — the
            // client-facing message stays terse and never includes credentials.
            logger?.LogWarning(ex, "MySQL connection attempt to {Host}:{Port} failed", host, port);

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
        await using var connection = new MySqlConnection(BuildServerConnectionString(host, port, username, password));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{database}`";
        await command.ExecuteNonQueryAsync();
    }
}
