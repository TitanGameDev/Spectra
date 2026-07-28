using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spectra.Api.Services;

public record AppVersionInfo(
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("deployedAt")] DateTimeOffset DeployedAt);

public record UpdateRunStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt);

// Reads/writes the small set of files deploy/install.sh and deploy/update.sh
// use to hand information back to the app — see the README's "One-click
// update" section for the full flow. This service never performs the update
// itself (that needs root; the backend deliberately runs unprivileged) — it
// only ever reads status files a root-owned process wrote, and writes one
// flag file that a separate systemd .path unit watches for. Entirely inert
// (IsConfigured false) on any deployment that isn't using install.sh, e.g.
// local dev — every method below degrades to "not available" rather than
// throwing, same convention as the EXO-certificate/database-health checks
// elsewhere in the app.
public class AppUpdateService(IConfiguration configuration, ILogger<AppUpdateService> logger)
{
    private static readonly TimeSpan RemoteCheckTimeout = TimeSpan.FromSeconds(5);

    private string? DataDir => configuration["Update:DataDir"];
    private string? SrcDir => configuration["Update:SrcDir"];

    public bool IsConfigured => !string.IsNullOrEmpty(DataDir);

    public AppVersionInfo? ReadCurrentVersion()
    {
        var path = VersionFilePath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AppVersionInfo>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read {Path}", path);
            return null;
        }
    }

    public UpdateRunStatus ReadRunStatus()
    {
        if (!IsConfigured)
        {
            return new UpdateRunStatus("unavailable", null, null, null);
        }
        var path = Path.Combine(DataDir!, "update-status.json");
        if (!File.Exists(path))
        {
            return new UpdateRunStatus("idle", null, null, null);
        }
        try
        {
            var status = JsonSerializer.Deserialize<UpdateRunStatus>(File.ReadAllText(path));
            return status ?? new UpdateRunStatus("idle", null, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read {Path}", path);
            return new UpdateRunStatus("idle", null, null, null);
        }
    }

    // Best-effort — a network hiccup or missing git checkout just means the
    // caller shows "unknown" rather than an "update available" claim, never
    // an error. Compares against the currently deployed commit from
    // version.json, so this is meaningless (returns null) until at least one
    // successful install/update has run.
    public async Task<string?> GetLatestRemoteCommitAsync(CancellationToken ct = default)
    {
        var currentVersion = ReadCurrentVersion();
        if (SrcDir is null || currentVersion is null || !Directory.Exists(SrcDir))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = SrcDir,
        };
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("origin");
        psi.ArgumentList.Add(currentVersion.Ref);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = new CancellationTokenSource(RemoteCheckTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode != 0)
            {
                return null;
            }
            var stdout = await stdoutTask;
            // Format: "<sha>\tref/heads/<branch>" — the SHA is everything
            // before the first whitespace on the first line.
            var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstLine?.Split('\t', ' ').FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "git ls-remote check failed");
            return null;
        }
    }

    public (bool Queued, string? Error) RequestUpdate(string requestedByEmail)
    {
        if (!IsConfigured)
        {
            return (false, "Update automation isn't configured on this deployment (not installed via deploy/install.sh).");
        }

        var status = ReadRunStatus();
        if (status.State == "running")
        {
            return (false, "An update is already in progress.");
        }

        try
        {
            var flagPath = Path.Combine(DataDir!, "update-requested");
            var payload = JsonSerializer.Serialize(new { requestedByEmail, requestedAt = DateTimeOffset.UtcNow });
            File.WriteAllText(flagPath, payload);
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write update request flag");
            return (false, "Failed to request an update — the backend couldn't write to its data directory.");
        }
    }

    private string? VersionFilePath => DataDir is null ? null : Path.Combine(DataDir, "version.json");
}
