using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Spectra.Api.Services;

public record SccDlpPolicyDto(string? Name, bool? Enabled, string? Mode);
public record SccRetentionPolicyDto(string? Name, bool? Enabled, string? Mode);
public record SccAlertPolicyDto(string? Name, string? Category, string? Severity, bool? Disabled, List<string>? NotifyUser);

public record SccCollectionResultDto(
    List<SccDlpPolicyDto>? DlpPolicies,
    List<SccRetentionPolicyDto>? RetentionPolicies,
    List<SccAlertPolicyDto>? AlertPolicies);

// Security & Compliance access isn't set up yet for this tenant — mirrors
// ExoAccessException, distinguishing "not ready" (role hasn't propagated)
// from a real bug. In practice this fires far less often than the EXO
// version, since by the time SCC collection runs, ExoRoleAssigned (the same
// gate) has usually already succeeded and propagated for the EXO call.
public class SccAccessException(string message) : Exception(message);

// Anything else: pwsh missing, the ExchangeOnlineManagement module not
// installed, the certificate missing/misconfigured, a timeout, or a cmdlet
// batch that didn't produce parseable JSON.
public class SccCollectionException(string message) : Exception(message);

// Collects Microsoft Purview (Security & Compliance) configuration —
// DLP policies, retention policies, alert policies — via app-only
// (certificate) PowerShell auth against Connect-IPPSSession, a separate
// session/endpoint from Exchange Online PowerShell's Connect-ExchangeOnline
// but using the exact same app registration, certificate, and Global Reader
// role assignment (see ExoPowerShellClient.cs and
// GraphAppClient.EnsureGlobalReaderRoleAssignedAsync) — no separate setup.
// Kept as a sibling client with its own script rather than merged into
// ExoPowerShellClient, since loading both EXO and IPPS cmdlets in one
// session risks name collisions (e.g. Get-OrganizationConfig exists in both).
public class SccPowerShellClient(IActiveAzureAdConfigProvider azureAdConfig, IConfiguration configuration, ILogger<SccPowerShellClient> logger)
{
    // No per-mailbox enumeration here (unlike EXO's Get-MailboxAuditBypassAssociation),
    // so no need for EXO's bumped 150s.
    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(90);

    public async Task<SccCollectionResultDto> CollectAsync(string organizationDomain, CancellationToken ct = default)
    {
        var appId = azureAdConfig.BackendClientId;
        var certPath = configuration["Exo:CertificatePath"];
        var certPassword = configuration["Exo:CertificatePassword"];

        if (string.IsNullOrEmpty(appId))
        {
            throw new SccCollectionException("Azure AD isn't configured yet — see Settings → Authentication.");
        }
        if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(certPassword))
        {
            throw new SccCollectionException("Exo:CertificatePath / Exo:CertificatePassword are not configured — see README.");
        }
        if (!File.Exists(certPath))
        {
            throw new SccCollectionException($"EXO certificate not found at {certPath} — see README.");
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "Collect-SccSecurityData.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new SccCollectionException($"Security & Compliance collection script not found at {scriptPath}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-Organization");
        psi.ArgumentList.Add(organizationDomain);
        psi.ArgumentList.Add("-AppId");
        psi.ArgumentList.Add(appId);
        psi.ArgumentList.Add("-CertificatePath");
        psi.ArgumentList.Add(certPath);
        // Only the secret goes via the environment — never a CLI arg, which
        // would be visible to anything that can list this host's processes.
        psi.Environment["SCC_CERT_PASSWORD"] = certPassword;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new SccCollectionException("pwsh (PowerShell 7) isn't installed on this server, or isn't on PATH — see README.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(CollectionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new SccCollectionException($"Security & Compliance collection timed out after {CollectionTimeout.TotalSeconds:0}s.");
        }

        var stderrText = stderr.ToString();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderrText))
        {
            ClassifyAndThrow(stderrText, process.ExitCode);
        }

        var stdoutText = stdout.ToString().Trim();
        try
        {
            var result = JsonSerializer.Deserialize<SccCollectionResultDto>(stdoutText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (result is null)
            {
                throw new SccCollectionException("Security & Compliance collection returned no data.");
            }
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Security & Compliance collection output for {Organization}", organizationDomain);
            throw new SccCollectionException($"Security & Compliance collection returned unparseable output: {ex.Message}");
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have already exited.
        }
    }

    private static void ClassifyAndThrow(string stderrText, int exitCode)
    {
        if (stderrText.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("does not have sufficient permission", StringComparison.OrdinalIgnoreCase)
            || stderrText.Contains("Unable to acquire token", StringComparison.OrdinalIgnoreCase)
            || (stderrText.Contains("AADSTS", StringComparison.OrdinalIgnoreCase) && stderrText.Contains("consent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SccAccessException($"Security & Compliance access denied — {Summarize(stderrText)}");
        }

        if (stderrText.Contains("ExchangeOnlineManagement", StringComparison.OrdinalIgnoreCase)
            && (stderrText.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                || stderrText.Contains("is not installed", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SccCollectionException("ExchangeOnlineManagement module not installed — run Install-Module ExchangeOnlineManagement -Scope AllUsers.");
        }

        throw new SccCollectionException($"Security & Compliance collection failed (exit {exitCode}) — {Summarize(stderrText)}");
    }

    // PowerShell error streams are often multi-line with a lot of stack-trace
    // noise — keep only the first non-empty line for the message that ends up
    // in SccLastError, since that's what's actually shown to users.
    private static string Summarize(string stderrText)
    {
        var firstLine = stderrText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "unknown error" : firstLine;
    }
}
