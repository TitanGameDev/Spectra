using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Spectra.Api.Auth;
using Spectra.Api.Data;
using Spectra.Api.Services;

// Loads backend/.env into process environment variables (if present) so
// ASP.NET Core's built-in env-var config provider can pick up AzureAd__*
// and Cors__* keys. No-op in environments where the file doesn't exist
// (e.g. production, where real config comes from a secure vault instead).
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim("spectra_admin", "true"));
});

// Local, gitignored — persists Data Protection keys across restarts so anything
// encrypted with them (the active-database marker, stored SQL Server passwords)
// stays decryptable.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Spectra.DatabaseConnection"));

builder.Services.AddSingleton<IActiveDatabaseProvider, ActiveDatabaseProvider>();
builder.Services.AddSingleton<DatabaseHealth>();

// Factory-based (not a fixed connection string) so IActiveDatabaseProvider's state is
// re-read on every scope resolution (~every request) — this is what lets an admin
// switch databases from Settings and have it take effect immediately, no restart.
builder.Services.AddDbContext<SpectraDbContext>((sp, options) =>
{
    var active = sp.GetRequiredService<IActiveDatabaseProvider>();
    switch (active.Kind)
    {
        case "sqlserver":
            options.UseSqlServer(active.ConnectionString);
            break;
        case "mysql":
            options.UseMySql(active.ConnectionString, ServerVersion.Parse(active.MySqlServerVersion));
            break;
        default:
            options.UseSqlite(active.ConnectionString);
            break;
    }
});
builder.Services.AddScoped<IClaimsTransformation, SpectraClaimsTransformation>();

// Calls Graph as Spectra's own app registration (client-credentials flow)
// against a specific customer tenant — see GraphAppClient.cs.
builder.Services.AddHttpClient<GraphAppClient>();

// Shells out to pwsh for Exchange Online PowerShell collection — no HttpClient
// needed, see ExoPowerShellClient.cs.
builder.Services.AddSingleton<ExoPowerShellClient>();

// Shells out to pwsh for Security & Compliance PowerShell collection — a
// sibling to ExoPowerShellClient using the same cert/role, see SccPowerShellClient.cs.
builder.Services.AddSingleton<SccPowerShellClient>();

// Direct DNS TXT-record lookups for SPF/DMARC checks — no HttpClient, no
// auth, see DnsCheckClient.cs.
builder.Services.AddSingleton<DnsCheckClient>();

// Trust X-Forwarded-For/-Proto from nginx so the app sees the real client IP
// and scheme. Defaults to trusting only loopback proxies, which matches
// nginx and Kestrel running on the same host — see deploy/nginx/spectra.conf.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// No silent fallback in non-dev environments — a missing origin should fail
// startup loudly rather than let CORS quietly reject (or worse, misconfigure) prod traffic.
var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"];
if (string.IsNullOrWhiteSpace(allowedOrigin))
{
    if (builder.Environment.IsDevelopment())
    {
        allowedOrigin = "http://localhost:5173";
    }
    else
    {
        throw new InvalidOperationException("Cors:AllowedOrigin must be configured outside Development.");
    }
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(allowedOrigin)
              .WithMethods("GET", "POST", "PUT")
              .WithHeaders("Authorization", "Content-Type"));
});

// Backstop against abuse; nginx also rate-limits at the edge (see deploy/nginx/spectra.conf).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// Guards CollectCustomerDataAsync per customer — it deletes and re-inserts
// that customer's CustomerUsers rows, which isn't safe to run concurrently
// (two overlapping collections race on the same delete, and the second one's
// DELETE affects 0 rows since the first already removed them, throwing
// DbUpdateConcurrencyException). This can genuinely happen: adding a customer
// triggers an immediate collection, and if the admin clicks Grant consent
// and it completes quickly, the consent-callback tab's auto-collect (see
// Settings.tsx) can land while that first one is still in flight.
var collectionLocks = new ConcurrentDictionary<int, SemaphoreSlim>();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SpectraDbContext>();
    var active = scope.ServiceProvider.GetRequiredService<IActiveDatabaseProvider>();
    var health = scope.ServiceProvider.GetRequiredService<DatabaseHealth>();
    try
    {
        if (active.Kind is "sqlserver" or "mysql")
        {
            // SQL Server/MySQL databases configured through Settings are provisioned
            // with EnsureCreated (see the /provision endpoint), not migrations — this
            // just covers the case where the app restarts after a prior cutover.
            db.Database.EnsureCreated();

            // EnsureCreated only creates the schema when the database has no tables
            // at all — it silently no-ops on one that's already provisioned, even if
            // the model has grown new columns/tables since (like Customer.TenantId
            // and CustomerUsers here). Patch those in idempotently so an
            // already-cutover external database doesn't get left behind.
            await ApplyExternalSchemaPatchesAsync(db, active.Kind);
        }
        else
        {
            db.Database.Migrate();
        }
        health.MarkHealthy();
    }
    catch (Exception ex)
    {
        // A configured external database can go bad after the fact — dropped
        // tables, revoked credentials, the server itself being down — with no
        // way to reach it from inside the app to fix it. Don't crash the whole
        // process over that: start anyway, in a degraded state, so the
        // frontend's database-setup screen (driven by /api/system/status) can
        // guide a reset instead of the app being completely unreachable.
        app.Logger.LogError(ex, "Database unavailable at startup (provider: {Kind})", active.Kind);
        health.MarkUnhealthy(ex.Message);
    }
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Never leak stack traces/exception details to a public client.
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"An unexpected error occurred."}""");
    }));
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    await next();
});

app.UseRateLimiter();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous();

app.MapGet("/api/system/status", (DatabaseHealth health) =>
{
    return Results.Ok(new { databaseHealthy = health.IsHealthy, databaseError = health.Error });
}).RequireAuthorization();

// Not tied to any customer — the EXO certificate is one app-wide file, so
// this is a plain synchronous file read, not part of per-customer
// collection. Surfaced in the Security tab's Email Security sub-tab as an
// early warning well ahead of the certificate's 2-year validity running out.
app.MapGet("/api/system/exo-certificate-status", (IConfiguration configuration) =>
{
    var certPath = configuration["Exo:CertificatePath"];
    var certPassword = configuration["Exo:CertificatePassword"];
    if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(certPassword))
    {
        return Results.Ok(new { configured = false, expiresAt = (DateTimeOffset?)null, daysRemaining = (int?)null, expiringSoon = false, error = (string?)null });
    }

    try
    {
        using var cert = new X509Certificate2(certPath, certPassword);
        var daysRemaining = (int)(cert.NotAfter - DateTime.UtcNow).TotalDays;
        return Results.Ok(new
        {
            configured = true,
            expiresAt = (DateTimeOffset?)cert.NotAfter,
            daysRemaining = (int?)daysRemaining,
            expiringSoon = daysRemaining <= 60,
            error = (string?)null,
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { configured = true, expiresAt = (DateTimeOffset?)null, daysRemaining = (int?)null, expiringSoon = false, error = (string?)ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/api/system/reset-to-sqlite", async (
    ClaimsPrincipal user,
    DatabaseHealth health,
    IActiveDatabaseProvider activeProvider,
    IConfiguration configuration,
    ILogger<Program> logger) =>
{
    // Falling back to a fresh local database when the configured one is broken
    // has no admin concept to check yet (that's exactly what's broken) — allow
    // it for anyone while unhealthy. Once healthy, this is admin-only like any
    // other database change.
    var isAdmin = user.FindFirstValue("spectra_admin") == "true";
    if (health.IsHealthy && !isAdmin)
    {
        return Results.Forbid();
    }

    var sqliteConnectionString = configuration.GetConnectionString("Default") ?? "Data Source=spectra.db";
    try
    {
        var targetOptionsBuilder = new DbContextOptionsBuilder<SpectraDbContext>();
        targetOptionsBuilder.UseSqlite(sqliteConnectionString);
        await using var targetDb = new SpectraDbContext(targetOptionsBuilder.Options);
        await targetDb.Database.MigrateAsync();

        activeProvider.SwitchTo("sqlite", sqliteConnectionString);
        health.MarkHealthy();
        return Results.Ok(new { activeProvider = "sqlite" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to reset to SQLite");
        return Results.Problem($"Failed to switch to SQLite: {ex.Message}", statusCode: 500);
    }
}).RequireAuthorization();

app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    // Curated fields only — avoid handing the client the full raw claims set
    // (tenant/object IDs, app IDs, etc.) it has no need for.
    return Results.Ok(new
    {
        name = user.FindFirstValue("name") ?? user.Identity?.Name,
        email = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username"),
        isAdmin = user.FindFirstValue("spectra_admin") == "true",
    });
}).RequireAuthorization();

app.MapGet("/api/settings", async (SpectraDbContext db) =>
{
    var settings = await db.Settings.SingleOrDefaultAsync();
    return Results.Ok(new
    {
        adminGroupId = settings?.AdminGroupId,
        adminGroupDisplayName = settings?.AdminGroupDisplayName,
        updatedAt = settings?.UpdatedAt,
        updatedByEmail = settings?.UpdatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/settings", async (UpdateSettingsRequest request, ClaimsPrincipal user, SpectraDbContext db) =>
{
    if (!string.IsNullOrEmpty(request.AdminGroupId) && !Guid.TryParse(request.AdminGroupId, out _))
    {
        return Results.BadRequest(new { error = "adminGroupId must be a valid Entra group Object ID (GUID)." });
    }

    var settings = await db.Settings.SingleOrDefaultAsync();
    if (settings is null)
    {
        settings = new AppSettings();
        db.Settings.Add(settings);
    }

    settings.AdminGroupId = string.IsNullOrEmpty(request.AdminGroupId) ? null : request.AdminGroupId;
    settings.AdminGroupDisplayName = string.IsNullOrEmpty(request.AdminGroupId) ? null : request.AdminGroupDisplayName;
    settings.UpdatedAt = DateTimeOffset.UtcNow;
    settings.UpdatedByEmail = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username");

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        adminGroupId = settings.AdminGroupId,
        adminGroupDisplayName = settings.AdminGroupDisplayName,
        updatedAt = settings.UpdatedAt,
        updatedByEmail = settings.UpdatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/customers", async (SpectraDbContext db) =>
{
    // Full detail for the admin Settings management view.
    var customers = await db.Customers
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            c.Id,
            c.Name,
            c.TenantId,
            c.ConsentGranted,
            c.LastSyncedAt,
            c.LastSyncError,
            c.CreatedAt,
            c.CreatedByEmail,
        })
        .ToListAsync();
    return Results.Ok(customers);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/customers/summary", async (SpectraDbContext db) =>
{
    // Minimal — just enough for every signed-in user's customer switcher dropdown,
    // plus a couple of flags cheap enough to always include that let far-flung
    // pages (e.g. Users.tsx's Mailboxes sub-tab) show accurate guidance without
    // a second fetch.
    var customers = await db.Customers
        .OrderBy(c => c.Name)
        .Select(c => new { c.Id, c.Name, c.MailboxDataConcealed })
        .ToListAsync();
    return Results.Ok(customers);
}).RequireAuthorization();

app.MapGet("/api/customers/{id:int}/users", async (int id, SpectraDbContext db) =>
{
    if (!await db.Customers.AnyAsync(c => c.Id == id))
    {
        return Results.NotFound();
    }

    var users = await db.CustomerUsers
        .Where(u => u.CustomerId == id)
        .OrderBy(u => u.DisplayName)
        .ToListAsync();

    return Results.Ok(users.Select(u => new
    {
        u.Id,
        u.GraphUserId,
        u.DisplayName,
        u.Mail,
        u.UserPrincipalName,
        u.JobTitle,
        u.Department,
        u.OfficeLocation,
        u.AccountEnabled,
        u.CreatedDateTime,
        u.SyncedAt,
        Mailbox = u.MailboxSizeBytes is null && u.MailboxItemCount is null && u.HasArchiveMailbox is null
            ? null
            : new { SizeBytes = u.MailboxSizeBytes, ItemCount = u.MailboxItemCount, HasArchive = u.HasArchiveMailbox },
        Licenses = DeserializeLicenses(u.LicensesJson),
        Mfa = DeserializeMfa(u.MfaJson),
        ForwardingRules = DeserializeForwardingRules(u.ForwardingRulesJson),
        InboxRules = DeserializeInboxRules(u.InboxRulesJson),
    }));
}).RequireAuthorization();

app.MapGet("/api/customers/{id:int}/security", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        SecureScore = string.IsNullOrEmpty(customer.SecureScoreJson)
            ? null
            : JsonSerializer.Deserialize<GraphSecureScoreDto>(customer.SecureScoreJson),
        SecureScoreControls = BuildSecureScoreControls(customer),
        ConditionalAccessPolicies = string.IsNullOrEmpty(customer.ConditionalAccessPoliciesJson)
            ? []
            : (JsonSerializer.Deserialize<List<GraphConditionalAccessPolicyDto>>(customer.ConditionalAccessPoliciesJson) ?? [])
                .Select(NormalizeCaPolicy),
    });
}).RequireAuthorization();

// Separate from /security above rather than folded into it — Graph consent
// (ConsentGranted) and Exchange Online access (ExoRoleAssigned) are
// independent setup tracks that can each succeed or fail on their own
// schedule, and keeping their status fields apart makes both easier to
// reason about on the frontend.
app.MapGet("/api/customers/{id:int}/email-security", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var mailboxForwarding = DeserializeExo<List<ExoMailboxForwardingDto>>(customer.ExoMailboxForwardingJson);
    var transportRules = DeserializeExo<List<ExoTransportRuleDto>>(customer.ExoTransportRulesJson);

    var checks = customer.ExoLastCollectedAt is null
        ? []
        : OrcaCheckEvaluator.Evaluate(
            DeserializeExo<ExoOrganizationConfigDto>(customer.ExoOrganizationConfigJson),
            DeserializeExo<List<ExoAcceptedDomainDto>>(customer.ExoAcceptedDomainsJson),
            DeserializeExo<List<ExoAntiPhishPolicyDto>>(customer.ExoAntiPhishPoliciesJson),
            DeserializeExo<List<ExoSafeLinksPolicyDto>>(customer.ExoSafeLinksPoliciesJson),
            DeserializeExo<List<ExoSafeAttachmentPolicyDto>>(customer.ExoSafeAttachmentPoliciesJson),
            DeserializeExo<List<ExoHostedContentFilterPolicyDto>>(customer.ExoHostedContentFilterPoliciesJson),
            DeserializeExo<List<ExoHostedOutboundSpamFilterPolicyDto>>(customer.ExoHostedOutboundSpamFilterPoliciesJson),
            DeserializeExo<List<ExoMalwareFilterPolicyDto>>(customer.ExoMalwareFilterPoliciesJson),
            DeserializeExo<List<ExoDkimSigningConfigDto>>(customer.ExoDkimSigningConfigsJson),
            transportRules,
            DeserializeExo<List<ExoSharingPolicyDto>>(customer.ExoSharingPoliciesJson),
            DeserializeExo<List<ExoHostedConnectionFilterPolicyDto>>(customer.ExoHostedConnectionFilterPoliciesJson),
            DeserializeExo<ExoAdminAuditLogConfigDto>(customer.ExoAdminAuditLogConfigJson),
            DeserializeExo<ExoAtpPolicyForO365Dto>(customer.ExoAtpPolicyForO365Json),
            DeserializeExo<List<ExoRemoteDomainDto>>(customer.ExoRemoteDomainsJson),
            DeserializeExo<List<ExoMailboxAuditBypassDto>>(customer.ExoMailboxAuditBypassJson),
            mailboxForwarding);

    return Results.Ok(new
    {
        customer.ExoRoleAssigned,
        customer.ExoLastCollectedAt,
        customer.ExoLastError,
        Checks = checks,
        // Raw collected data, not just pass/fail checks — lets the frontend
        // show mailbox forwarding (Forwarding Rules tab) and mail flow rules
        // (Mail Flow Rules tab) as browsable tables, not just baked into a
        // single check each.
        MailboxForwarding = mailboxForwarding ?? [],
        TransportRules = transportRules ?? [],
    });
}).RequireAuthorization();

// DLP/retention/alert policy checks sourced from Security & Compliance
// PowerShell (Connect-IPPSSession, see SccPowerShellClient) — a sibling
// session to the EXO one above, using the exact same certificate and Global
// Reader role, so it shares ExoRoleAssigned as its setup-gate rather than
// having its own.
app.MapGet("/api/customers/{id:int}/compliance-security", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var checks = customer.SccLastCollectedAt is null
        ? []
        : SccCheckEvaluator.Evaluate(
            DeserializeExo<List<SccDlpPolicyDto>>(customer.SccDlpPoliciesJson),
            DeserializeExo<List<SccRetentionPolicyDto>>(customer.SccRetentionPoliciesJson),
            DeserializeExo<List<SccAlertPolicyDto>>(customer.SccAlertPoliciesJson));

    return Results.Ok(new
    {
        customer.ExoRoleAssigned,
        customer.SccLastCollectedAt,
        customer.SccLastError,
        Checks = checks,
    });
}).RequireAuthorization();

// Identity/RBAC checks sourced from Graph, not Exchange Online PowerShell —
// unlike /email-security above, there's no separate access-grant/setup
// track here: everything this needs (RoleManagement.ReadWrite.Directory,
// Policy.Read.All) is already covered by the app's existing permission set,
// so this degrades the same way the rest of the Graph-sourced Security tab
// data does (a missing permission just means individual checks show "info").
app.MapGet("/api/customers/{id:int}/identity-security", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var globalAdmins = DeserializeExo<List<GraphUserDto>>(customer.GlobalAdminsJson);
    var caPolicies = DeserializeExo<List<GraphConditionalAccessPolicyDto>>(customer.ConditionalAccessPoliciesJson);

    // MFA registration is already collected per-user (CustomerUsers.MfaJson)
    // for the existing MFA sub-tab — reused here rather than re-fetched, so
    // "admins without MFA" costs nothing extra to collect.
    var mfaRows = await db.CustomerUsers
        .Where(u => u.CustomerId == id)
        .Select(u => new { u.GraphUserId, u.MfaJson })
        .ToListAsync();
    var mfaByGraphUserId = mfaRows.ToDictionary(u => u.GraphUserId, u => DeserializeMfa(u.MfaJson)?.IsMfaRegistered);

    var checks = DirectoryCheckEvaluator.Evaluate(globalAdmins, customer.SecurityDefaultsEnabled, caPolicies, mfaByGraphUserId);

    return Results.Ok(new { Checks = checks });
}).RequireAuthorization();

// SPF/DMARC DNS checks — live public DNS lookups against the tenant's
// verified domains, no Graph/EXO permission or setup involved at all.
app.MapGet("/api/customers/{id:int}/domain-security", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var domainChecks = DeserializeExo<List<DnsRecordCheckDto>>(customer.DnsRecordChecksJson);
    var checks = DnsCheckEvaluator.Evaluate(domainChecks);

    return Results.Ok(new { Checks = checks });
}).RequireAuthorization();

static T? DeserializeExo<T>(string? json)
{
    if (string.IsNullOrEmpty(json))
    {
        return default;
    }
    try
    {
        return JsonSerializer.Deserialize<T>(json);
    }
    catch (JsonException)
    {
        return default;
    }
}

// Joins the Secure Score control catalog (title, remediation, max score —
// mostly static) against the tenant's actually-achieved score per control
// (from the latest snapshot) to produce an actionable checklist, biggest
// improvement opportunity first. Both come from the same SecurityEvents.Read.All
// permission but are two separate Graph calls/storage fields (see
// CollectCustomerDataAsync), so either can be missing independently.
static List<object> BuildSecureScoreControls(Customer customer)
{
    if (string.IsNullOrEmpty(customer.SecureScoreControlProfilesJson))
    {
        return [];
    }

    List<GraphSecureScoreControlProfileDto> profiles;
    try
    {
        profiles = JsonSerializer.Deserialize<List<GraphSecureScoreControlProfileDto>>(customer.SecureScoreControlProfilesJson) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }

    var achieved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrEmpty(customer.SecureScoreJson))
    {
        try
        {
            var score = JsonSerializer.Deserialize<GraphSecureScoreDto>(customer.SecureScoreJson);
            foreach (var control in score?.ControlScores ?? [])
            {
                achieved[control.ControlName] = control.Score;
            }
        }
        catch (JsonException)
        {
            // Fall through with an empty achieved-score map — controls still show with 0 achieved.
        }
    }

    return profiles
        .Where(p => !p.Deprecated)
        .Select(p => new
        {
            p.Id,
            p.Title,
            Category = p.ControlCategory,
            AchievedScore = achieved.GetValueOrDefault(p.Id, 0),
            p.MaxScore,
            p.Rank,
            p.Tier,
            p.ImplementationCost,
            p.UserImpact,
            p.ActionType,
            p.Remediation,
            p.RemediationImpact,
            Threats = p.Threats ?? [],
        })
        .OrderByDescending(c => c.MaxScore - c.AchievedScore)
        .ToList<object>();
}

// A customer whose data was last collected before the CA policy detail
// fields existed has stored JSON missing them, which deserializes those
// list properties as null — coerce to [] so the API's contract (and the
// frontend's non-nullable array types) always holds, regardless of when the
// stored data was collected.
static GraphConditionalAccessPolicyDto NormalizeCaPolicy(GraphConditionalAccessPolicyDto p) => p with
{
    IncludeUsers = p.IncludeUsers ?? [],
    ExcludeUsers = p.ExcludeUsers ?? [],
    IncludeGroups = p.IncludeGroups ?? [],
    ExcludeGroups = p.ExcludeGroups ?? [],
    IncludeRoles = p.IncludeRoles ?? [],
    ExcludeRoles = p.ExcludeRoles ?? [],
    IncludeApplications = p.IncludeApplications ?? [],
    ExcludeApplications = p.ExcludeApplications ?? [],
    ClientAppTypes = p.ClientAppTypes ?? [],
    BuiltInControls = p.BuiltInControls ?? [],
};

app.MapGet("/api/customers/{id:int}/consent-url", async (int id, SpectraDbContext db, IConfiguration configuration) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var clientId = configuration["AzureAd:ClientId"];
    var redirectUri = configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
    // "state" round-trips through Entra unchanged, back onto the redirect URI as a
    // query param — this is how the tab the consent popup opens in knows which
    // customer just got consent, without needing its own signed-in session (it
    // won't have one; see ConsentCallback.tsx on the frontend).
    var consentUrl =
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(customer.TenantId)}/adminconsent" +
        $"?client_id={Uri.EscapeDataString(clientId ?? "")}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(customer.Id.ToString())}";

    return Results.Ok(new { consentUrl });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/customers", async (
    CreateCustomerRequest request,
    ClaimsPrincipal user,
    SpectraDbContext db,
    GraphAppClient graphClient,
    ExoPowerShellClient exoClient,
    DnsCheckClient dnsClient,
    SccPowerShellClient sccClient,
    ILogger<Program> logger) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrEmpty(name))
    {
        return Results.BadRequest(new { error = "Name is required." });
    }
    if (name.Length > 200)
    {
        return Results.BadRequest(new { error = "Name must be 200 characters or fewer." });
    }

    var tenantId = request.TenantId?.Trim();
    if (string.IsNullOrEmpty(tenantId) || !Guid.TryParse(tenantId, out _))
    {
        return Results.BadRequest(new { error = "A valid Entra tenant ID (a GUID) is required." });
    }

    var customer = new Customer
    {
        Name = name,
        TenantId = tenantId,
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedByEmail = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "",
    };
    db.Customers.Add(customer);
    await db.SaveChangesAsync();

    // Immediate one-time collection attempt — failure is non-fatal (e.g. the
    // customer's admin hasn't granted consent yet); the customer is saved
    // either way and collection can be retried from Settings.
    await CollectCustomerDataAsync(customer, db, graphClient, exoClient, dnsClient, sccClient, logger, collectionLocks);

    return Results.Created($"/api/customers/{customer.Id}", new
    {
        customer.Id,
        customer.Name,
        customer.TenantId,
        customer.ConsentGranted,
        customer.LastSyncedAt,
        customer.LastSyncError,
        customer.CreatedAt,
        customer.CreatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/customers/{id:int}/collect", async (
    int id,
    SpectraDbContext db,
    GraphAppClient graphClient,
    ExoPowerShellClient exoClient,
    DnsCheckClient dnsClient,
    SccPowerShellClient sccClient,
    ILogger<Program> logger) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    await CollectCustomerDataAsync(customer, db, graphClient, exoClient, dnsClient, sccClient, logger, collectionLocks);

    return Results.Ok(new
    {
        customer.Id,
        customer.Name,
        customer.TenantId,
        customer.ConsentGranted,
        customer.LastSyncedAt,
        customer.LastSyncError,
        customer.CreatedAt,
        customer.CreatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/settings/database", async (SpectraDbContext db, IActiveDatabaseProvider activeProvider) =>
{
    var config = await db.DatabaseConnections.SingleOrDefaultAsync();
    return Results.Ok(new
    {
        activeProvider = activeProvider.Kind,
        configured = config is not null,
        databaseType = config?.DatabaseType,
        host = config?.Host,
        port = config?.Port,
        databaseName = config?.DatabaseName,
        username = config?.Username,
        isProvisioned = config?.IsProvisioned ?? false,
        updatedAt = config?.UpdatedAt,
        updatedByEmail = config?.UpdatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/settings/database", async (
    DatabaseConnectionRequest request,
    ClaimsPrincipal user,
    SpectraDbContext db,
    IDataProtector protector,
    ILogger<Program> logger) =>
{
    if (request.DatabaseType is not ("sqlserver" or "mysql"))
    {
        return Results.BadRequest(new { error = "databaseType must be 'sqlserver' or 'mysql'." });
    }
    if (string.IsNullOrWhiteSpace(request.Host))
    {
        return Results.BadRequest(new { error = "Host is required." });
    }
    if (request.Port is <= 0 or > 65535)
    {
        return Results.BadRequest(new { error = "Port must be between 1 and 65535." });
    }
    if (string.IsNullOrWhiteSpace(request.DatabaseName) || !IsValidSqlIdentifier(request.DatabaseName))
    {
        return Results.BadRequest(new
        {
            error = "Database name must start with a letter or underscore and contain only letters, numbers, and underscores.",
        });
    }
    if (string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.BadRequest(new { error = "Username is required." });
    }
    if (string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Password is required." });
    }

    var testResult = request.DatabaseType == "mysql"
        ? await MySqlConnectionTester.TestAsync(request.Host, request.Port, request.DatabaseName, request.Username, request.Password, logger)
        : await SqlServerConnectionTester.TestAsync(request.Host, request.Port, request.DatabaseName, request.Username, request.Password, logger);
    if (!testResult.Reachable)
    {
        logger.LogWarning(
            "{DatabaseType} connection test failed for {Host}:{Port}/{Database} as {Username}: {Error}",
            request.DatabaseType, request.Host, request.Port, request.DatabaseName, request.Username, testResult.Error);
        return Results.BadRequest(new { error = $"Couldn't connect: {testResult.Error}" });
    }

    var config = await db.DatabaseConnections.SingleOrDefaultAsync();
    if (config is null)
    {
        config = new DatabaseConnectionConfig
        {
            DatabaseType = request.DatabaseType,
            Host = request.Host,
            DatabaseName = request.DatabaseName,
            Username = request.Username,
            EncryptedPassword = "",
            UpdatedByEmail = "",
        };
        db.DatabaseConnections.Add(config);
    }

    config.DatabaseType = request.DatabaseType;
    config.Host = request.Host;
    config.Port = request.Port;
    config.DatabaseName = request.DatabaseName;
    config.Username = request.Username;
    config.EncryptedPassword = protector.Protect(request.Password);
    config.IsProvisioned = testResult.DatabaseExists;
    config.UpdatedAt = DateTimeOffset.UtcNow;
    config.UpdatedByEmail = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "";

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        databaseType = config.DatabaseType,
        host = config.Host,
        port = config.Port,
        databaseName = config.DatabaseName,
        username = config.Username,
        isProvisioned = config.IsProvisioned,
        needsCreation = !testResult.DatabaseExists,
    });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/settings/database/provision", async (
    SpectraDbContext db,
    IDataProtector protector,
    IActiveDatabaseProvider activeProvider,
    ClaimsPrincipal user,
    ILogger<Program> logger) =>
{
    var config = await db.DatabaseConnections.SingleOrDefaultAsync();
    if (config is null)
    {
        return Results.BadRequest(new { error = "No database connection configured yet." });
    }

    string password;
    try
    {
        password = protector.Unprotect(config.EncryptedPassword);
    }
    catch
    {
        return Results.Problem("Stored database credentials couldn't be decrypted. Reconfigure the connection.", statusCode: 500);
    }

    var isMySql = config.DatabaseType == "mysql";

    var testResult = isMySql
        ? await MySqlConnectionTester.TestAsync(config.Host, config.Port, config.DatabaseName, config.Username, password, logger)
        : await SqlServerConnectionTester.TestAsync(config.Host, config.Port, config.DatabaseName, config.Username, password, logger);
    if (!testResult.Reachable)
    {
        logger.LogWarning(
            "{DatabaseType} connection test failed during provisioning for {Host}:{Port}/{Database} as {Username}: {Error}",
            config.DatabaseType, config.Host, config.Port, config.DatabaseName, config.Username, testResult.Error);
        return Results.BadRequest(new { error = $"Couldn't reach the database server: {testResult.Error}" });
    }

    var targetConnectionString = isMySql
        ? MySqlConnectionTester.BuildConnectionString(config.Host, config.Port, config.DatabaseName, config.Username, password)
        : SqlServerConnectionTester.BuildConnectionString(config.Host, config.Port, config.DatabaseName, config.Username, password);

    try
    {
        if (!testResult.DatabaseExists)
        {
            if (isMySql)
            {
                await MySqlConnectionTester.CreateDatabaseAsync(config.Host, config.Port, config.DatabaseName, config.Username, password);
            }
            else
            {
                await SqlServerConnectionTester.CreateDatabaseAsync(config.Host, config.Port, config.DatabaseName, config.Username, password);
            }
        }

        var targetOptionsBuilder = new DbContextOptionsBuilder<SpectraDbContext>();
        if (isMySql)
        {
            targetOptionsBuilder.UseMySql(targetConnectionString, ServerVersion.Parse(testResult.ServerVersion));
        }
        else
        {
            targetOptionsBuilder.UseSqlServer(targetConnectionString);
        }
        await using var targetDb = new SpectraDbContext(targetOptionsBuilder.Options);
        await targetDb.Database.EnsureCreatedAsync();

        // Copy existing data so admin access, the admin group setting, customers,
        // and their collected data all survive the cutover — otherwise the admin
        // doing this could lose access, or customer data would just vanish.
        var users = await db.Users.AsNoTracking().ToListAsync();
        var settingsRows = await db.Settings.AsNoTracking().ToListAsync();
        var customers = await db.Customers.AsNoTracking().ToListAsync();
        var customerUsers = await db.CustomerUsers.AsNoTracking().ToListAsync();

        if (!await targetDb.Users.AnyAsync())
        {
            targetDb.Users.AddRange(users.Select(u => new AppUser
            {
                EntraObjectId = u.EntraObjectId,
                DisplayName = u.DisplayName,
                Email = u.Email,
                IsBootstrapAdmin = u.IsBootstrapAdmin,
                FirstSeenAt = u.FirstSeenAt,
            }));
        }
        if (!await targetDb.Settings.AnyAsync())
        {
            targetDb.Settings.AddRange(settingsRows.Select(s => new AppSettings
            {
                AdminGroupId = s.AdminGroupId,
                AdminGroupDisplayName = s.AdminGroupDisplayName,
                UpdatedAt = s.UpdatedAt,
                UpdatedByEmail = s.UpdatedByEmail,
            }));
        }
        if (!await targetDb.Customers.AnyAsync())
        {
            // Customer.Id is auto-generated in the target, so it won't match the
            // source IDs — build an old-id -> new-id map before copying
            // CustomerUsers, which references customers by that ID.
            var newCustomers = customers.Select(c => new Customer
            {
                Name = c.Name,
                TenantId = c.TenantId,
                ConsentGranted = c.ConsentGranted,
                LastSyncedAt = c.LastSyncedAt,
                LastSyncError = c.LastSyncError,
                SecureScoreJson = c.SecureScoreJson,
                SecureScoreControlProfilesJson = c.SecureScoreControlProfilesJson,
                ConditionalAccessPoliciesJson = c.ConditionalAccessPoliciesJson,
                GlobalAdminsJson = c.GlobalAdminsJson,
                SecurityDefaultsEnabled = c.SecurityDefaultsEnabled,
                DnsRecordChecksJson = c.DnsRecordChecksJson,
                ExoRoleAssigned = c.ExoRoleAssigned,
                ExoLastCollectedAt = c.ExoLastCollectedAt,
                ExoLastError = c.ExoLastError,
                ExoOrganizationConfigJson = c.ExoOrganizationConfigJson,
                ExoAcceptedDomainsJson = c.ExoAcceptedDomainsJson,
                ExoAntiPhishPoliciesJson = c.ExoAntiPhishPoliciesJson,
                ExoSafeLinksPoliciesJson = c.ExoSafeLinksPoliciesJson,
                ExoSafeAttachmentPoliciesJson = c.ExoSafeAttachmentPoliciesJson,
                ExoHostedContentFilterPoliciesJson = c.ExoHostedContentFilterPoliciesJson,
                ExoHostedOutboundSpamFilterPoliciesJson = c.ExoHostedOutboundSpamFilterPoliciesJson,
                ExoMalwareFilterPoliciesJson = c.ExoMalwareFilterPoliciesJson,
                ExoDkimSigningConfigsJson = c.ExoDkimSigningConfigsJson,
                ExoTransportRulesJson = c.ExoTransportRulesJson,
                ExoSharingPoliciesJson = c.ExoSharingPoliciesJson,
                ExoHostedConnectionFilterPoliciesJson = c.ExoHostedConnectionFilterPoliciesJson,
                ExoAdminAuditLogConfigJson = c.ExoAdminAuditLogConfigJson,
                ExoAtpPolicyForO365Json = c.ExoAtpPolicyForO365Json,
                ExoRemoteDomainsJson = c.ExoRemoteDomainsJson,
                ExoMailboxAuditBypassJson = c.ExoMailboxAuditBypassJson,
                ExoMailboxForwardingJson = c.ExoMailboxForwardingJson,
                SccLastCollectedAt = c.SccLastCollectedAt,
                SccLastError = c.SccLastError,
                SccDlpPoliciesJson = c.SccDlpPoliciesJson,
                SccRetentionPoliciesJson = c.SccRetentionPoliciesJson,
                SccAlertPoliciesJson = c.SccAlertPoliciesJson,
                CreatedAt = c.CreatedAt,
                CreatedByEmail = c.CreatedByEmail,
            }).ToList();
            targetDb.Customers.AddRange(newCustomers);
            await targetDb.SaveChangesAsync();

            var customerIdMap = customers
                .Zip(newCustomers, (oldC, newC) => (oldC.Id, newC.Id))
                .ToDictionary(pair => pair.Item1, pair => pair.Item2);

            targetDb.CustomerUsers.AddRange(customerUsers
                .Where(u => customerIdMap.ContainsKey(u.CustomerId))
                .Select(u => new CustomerUser
                {
                    CustomerId = customerIdMap[u.CustomerId],
                    GraphUserId = u.GraphUserId,
                    DisplayName = u.DisplayName,
                    Mail = u.Mail,
                    UserPrincipalName = u.UserPrincipalName,
                    JobTitle = u.JobTitle,
                    Department = u.Department,
                    OfficeLocation = u.OfficeLocation,
                    AccountEnabled = u.AccountEnabled,
                    CreatedDateTime = u.CreatedDateTime,
                    MailboxSizeBytes = u.MailboxSizeBytes,
                    MailboxItemCount = u.MailboxItemCount,
                    HasArchiveMailbox = u.HasArchiveMailbox,
                    LicensesJson = u.LicensesJson,
                    MfaJson = u.MfaJson,
                    ForwardingRulesJson = u.ForwardingRulesJson,
                    InboxRulesJson = u.InboxRulesJson,
                    SyncedAt = u.SyncedAt,
                }));
        }
        if (!await targetDb.DatabaseConnections.AnyAsync())
        {
            targetDb.DatabaseConnections.Add(new DatabaseConnectionConfig
            {
                DatabaseType = config.DatabaseType,
                Host = config.Host,
                Port = config.Port,
                DatabaseName = config.DatabaseName,
                Username = config.Username,
                EncryptedPassword = config.EncryptedPassword,
                IsProvisioned = true,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedByEmail = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "",
            });
        }

        await targetDb.SaveChangesAsync();

        config.IsProvisioned = true;
        await db.SaveChangesAsync();

        activeProvider.SwitchTo(config.DatabaseType, targetConnectionString, isMySql ? testResult.ServerVersion : null);

        return Results.Ok(new { success = true, activeProvider = config.DatabaseType });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to provision {DatabaseType} database {Host}:{Port}/{Database}", config.DatabaseType, config.Host, config.Port, config.DatabaseName);
        return Results.Problem($"Failed to provision the database: {ex.Message}", statusCode: 500);
    }
}).RequireAuthorization("AdminOnly");

app.Run();

static bool IsValidSqlIdentifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$");

// Idempotent patch-up for external databases provisioned before a model change —
// see the call site in the startup block for why EnsureCreated alone isn't enough.
// Every statement here is safe to run against a database that's already up to
// date (IF NOT EXISTS / COL_LENGTH guards), so this can run on every startup.
static async Task ApplyExternalSchemaPatchesAsync(SpectraDbContext db, string providerKind)
{
    if (providerKind == "mysql")
    {
        async Task<bool> ColumnExistsAsync(string table, string column)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column";
            var tableParam = cmd.CreateParameter();
            tableParam.ParameterName = "@table";
            tableParam.Value = table;
            cmd.Parameters.Add(tableParam);
            var columnParam = cmd.CreateParameter();
            columnParam.ParameterName = "@column";
            columnParam.Value = column;
            cmd.Parameters.Add(columnParam);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        }

        // TenantId can't be added as NOT NULL directly: MySQL's LONGTEXT columns
        // can't carry a literal DEFAULT, so a straight NOT NULL add fails against
        // a table that already has rows (from before this column existed).
        // Add nullable, backfill, then tighten.
        if (!await ColumnExistsAsync("Customers", "TenantId"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers ADD COLUMN TenantId LONGTEXT NULL");
            await db.Database.ExecuteSqlRawAsync("UPDATE Customers SET TenantId = '' WHERE TenantId IS NULL");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers MODIFY COLUMN TenantId LONGTEXT NOT NULL");
        }
        if (!await ColumnExistsAsync("Customers", "ConsentGranted"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Customers ADD COLUMN ConsentGranted TINYINT(1) NOT NULL DEFAULT 0");
        }
        if (!await ColumnExistsAsync("Customers", "LastSyncedAt"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers ADD COLUMN LastSyncedAt DATETIME(6) NULL");
        }
        if (!await ColumnExistsAsync("Customers", "LastSyncError"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers ADD COLUMN LastSyncError LONGTEXT NULL");
        }
        foreach (var (column, definition) in new[]
        {
            ("SecureScoreJson", "LONGTEXT NULL"),
            ("SecureScoreControlProfilesJson", "LONGTEXT NULL"),
            ("ConditionalAccessPoliciesJson", "LONGTEXT NULL"),
            ("GlobalAdminsJson", "LONGTEXT NULL"),
            ("SecurityDefaultsEnabled", "TINYINT(1) NULL"),
            ("DnsRecordChecksJson", "LONGTEXT NULL"),
            ("ExoRoleAssigned", "TINYINT(1) NOT NULL DEFAULT 0"),
            ("ExoLastCollectedAt", "DATETIME(6) NULL"),
            ("ExoLastError", "LONGTEXT NULL"),
            ("ExoOrganizationConfigJson", "LONGTEXT NULL"),
            ("ExoAcceptedDomainsJson", "LONGTEXT NULL"),
            ("ExoAntiPhishPoliciesJson", "LONGTEXT NULL"),
            ("ExoSafeLinksPoliciesJson", "LONGTEXT NULL"),
            ("ExoSafeAttachmentPoliciesJson", "LONGTEXT NULL"),
            ("ExoHostedContentFilterPoliciesJson", "LONGTEXT NULL"),
            ("ExoHostedOutboundSpamFilterPoliciesJson", "LONGTEXT NULL"),
            ("ExoMalwareFilterPoliciesJson", "LONGTEXT NULL"),
            ("ExoDkimSigningConfigsJson", "LONGTEXT NULL"),
            ("ExoTransportRulesJson", "LONGTEXT NULL"),
            ("ExoSharingPoliciesJson", "LONGTEXT NULL"),
            ("ExoHostedConnectionFilterPoliciesJson", "LONGTEXT NULL"),
            ("ExoAdminAuditLogConfigJson", "LONGTEXT NULL"),
            ("ExoAtpPolicyForO365Json", "LONGTEXT NULL"),
            ("ExoRemoteDomainsJson", "LONGTEXT NULL"),
            ("ExoMailboxAuditBypassJson", "LONGTEXT NULL"),
            ("ExoMailboxForwardingJson", "LONGTEXT NULL"),
            ("MailboxDataConcealed", "TINYINT(1) NOT NULL DEFAULT 0"),
            ("SccLastCollectedAt", "DATETIME(6) NULL"),
            ("SccLastError", "LONGTEXT NULL"),
            ("SccDlpPoliciesJson", "LONGTEXT NULL"),
            ("SccRetentionPoliciesJson", "LONGTEXT NULL"),
            ("SccAlertPoliciesJson", "LONGTEXT NULL"),
        })
        {
            if (!await ColumnExistsAsync("Customers", column))
            {
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE Customers ADD COLUMN {column} {definition}");
#pragma warning restore EF1002
            }
        }
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CustomerUsers (
                Id INT NOT NULL AUTO_INCREMENT,
                CustomerId INT NOT NULL,
                GraphUserId VARCHAR(255) NOT NULL,
                DisplayName LONGTEXT NULL,
                Mail LONGTEXT NULL,
                UserPrincipalName LONGTEXT NOT NULL,
                JobTitle LONGTEXT NULL,
                Department LONGTEXT NULL,
                OfficeLocation LONGTEXT NULL,
                AccountEnabled TINYINT(1) NOT NULL,
                CreatedDateTime DATETIME(6) NULL,
                MailboxSizeBytes BIGINT NULL,
                MailboxItemCount INT NULL,
                HasArchiveMailbox TINYINT(1) NULL,
                LicensesJson LONGTEXT NULL,
                MfaJson LONGTEXT NULL,
                ForwardingRulesJson LONGTEXT NULL,
                InboxRulesJson LONGTEXT NULL,
                SyncedAt DATETIME(6) NOT NULL,
                PRIMARY KEY (Id),
                UNIQUE KEY IX_CustomerUsers_CustomerId_GraphUserId (CustomerId, GraphUserId)
            ) CHARACTER SET utf8mb4
            """);
        foreach (var (column, definition) in new[]
        {
            ("Department", "LONGTEXT NULL"),
            ("OfficeLocation", "LONGTEXT NULL"),
            ("CreatedDateTime", "DATETIME(6) NULL"),
            ("MailboxSizeBytes", "BIGINT NULL"),
            ("MailboxItemCount", "INT NULL"),
            ("HasArchiveMailbox", "TINYINT(1) NULL"),
            ("LicensesJson", "LONGTEXT NULL"),
            ("MfaJson", "LONGTEXT NULL"),
            ("ForwardingRulesJson", "LONGTEXT NULL"),
            ("InboxRulesJson", "LONGTEXT NULL"),
        })
        {
            if (!await ColumnExistsAsync("CustomerUsers", column))
            {
                // column/definition come from the hardcoded array above, never user input —
                // DDL identifiers can't be parameterized anyway, so ExecuteSqlAsync wouldn't help.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE CustomerUsers ADD COLUMN {column} {definition}");
#pragma warning restore EF1002
            }
        }
    }
    else if (providerKind == "sqlserver")
    {
        await db.Database.ExecuteSqlRawAsync(
            "IF COL_LENGTH('Customers', 'TenantId') IS NULL ALTER TABLE Customers ADD TenantId nvarchar(max) NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync(
            "IF COL_LENGTH('Customers', 'ConsentGranted') IS NULL ALTER TABLE Customers ADD ConsentGranted bit NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(
            "IF COL_LENGTH('Customers', 'LastSyncedAt') IS NULL ALTER TABLE Customers ADD LastSyncedAt datetimeoffset NULL");
        await db.Database.ExecuteSqlRawAsync(
            "IF COL_LENGTH('Customers', 'LastSyncError') IS NULL ALTER TABLE Customers ADD LastSyncError nvarchar(max) NULL");
        foreach (var (column, definition) in new[]
        {
            ("SecureScoreJson", "nvarchar(max) NULL"),
            ("SecureScoreControlProfilesJson", "nvarchar(max) NULL"),
            ("ConditionalAccessPoliciesJson", "nvarchar(max) NULL"),
            ("GlobalAdminsJson", "nvarchar(max) NULL"),
            ("SecurityDefaultsEnabled", "bit NULL"),
            ("DnsRecordChecksJson", "nvarchar(max) NULL"),
            ("ExoRoleAssigned", "bit NOT NULL DEFAULT 0"),
            ("ExoLastCollectedAt", "datetimeoffset NULL"),
            ("ExoLastError", "nvarchar(max) NULL"),
            ("ExoOrganizationConfigJson", "nvarchar(max) NULL"),
            ("ExoAcceptedDomainsJson", "nvarchar(max) NULL"),
            ("ExoAntiPhishPoliciesJson", "nvarchar(max) NULL"),
            ("ExoSafeLinksPoliciesJson", "nvarchar(max) NULL"),
            ("ExoSafeAttachmentPoliciesJson", "nvarchar(max) NULL"),
            ("ExoHostedContentFilterPoliciesJson", "nvarchar(max) NULL"),
            ("ExoHostedOutboundSpamFilterPoliciesJson", "nvarchar(max) NULL"),
            ("ExoMalwareFilterPoliciesJson", "nvarchar(max) NULL"),
            ("ExoDkimSigningConfigsJson", "nvarchar(max) NULL"),
            ("ExoTransportRulesJson", "nvarchar(max) NULL"),
            ("ExoSharingPoliciesJson", "nvarchar(max) NULL"),
            ("ExoHostedConnectionFilterPoliciesJson", "nvarchar(max) NULL"),
            ("ExoAdminAuditLogConfigJson", "nvarchar(max) NULL"),
            ("ExoAtpPolicyForO365Json", "nvarchar(max) NULL"),
            ("ExoRemoteDomainsJson", "nvarchar(max) NULL"),
            ("ExoMailboxAuditBypassJson", "nvarchar(max) NULL"),
            ("ExoMailboxForwardingJson", "nvarchar(max) NULL"),
            ("MailboxDataConcealed", "bit NOT NULL DEFAULT 0"),
            ("SccLastCollectedAt", "datetimeoffset NULL"),
            ("SccLastError", "nvarchar(max) NULL"),
            ("SccDlpPoliciesJson", "nvarchar(max) NULL"),
            ("SccRetentionPoliciesJson", "nvarchar(max) NULL"),
            ("SccAlertPoliciesJson", "nvarchar(max) NULL"),
        })
        {
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"IF COL_LENGTH('Customers', '{column}') IS NULL ALTER TABLE Customers ADD {column} {definition}");
#pragma warning restore EF1002
        }
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('CustomerUsers', 'U') IS NULL
            CREATE TABLE CustomerUsers (
                Id int NOT NULL IDENTITY,
                CustomerId int NOT NULL,
                GraphUserId nvarchar(450) NOT NULL,
                DisplayName nvarchar(max) NULL,
                Mail nvarchar(max) NULL,
                UserPrincipalName nvarchar(max) NOT NULL,
                JobTitle nvarchar(max) NULL,
                Department nvarchar(max) NULL,
                OfficeLocation nvarchar(max) NULL,
                AccountEnabled bit NOT NULL,
                CreatedDateTime datetimeoffset NULL,
                MailboxSizeBytes bigint NULL,
                MailboxItemCount int NULL,
                HasArchiveMailbox bit NULL,
                LicensesJson nvarchar(max) NULL,
                MfaJson nvarchar(max) NULL,
                ForwardingRulesJson nvarchar(max) NULL,
                InboxRulesJson nvarchar(max) NULL,
                SyncedAt datetimeoffset NOT NULL,
                CONSTRAINT PK_CustomerUsers PRIMARY KEY (Id),
                CONSTRAINT IX_CustomerUsers_CustomerId_GraphUserId UNIQUE (CustomerId, GraphUserId)
            )
            """);
        foreach (var (column, definition) in new[]
        {
            ("Department", "nvarchar(max) NULL"),
            ("OfficeLocation", "nvarchar(max) NULL"),
            ("CreatedDateTime", "datetimeoffset NULL"),
            ("MailboxSizeBytes", "bigint NULL"),
            ("MailboxItemCount", "int NULL"),
            ("HasArchiveMailbox", "bit NULL"),
            ("LicensesJson", "nvarchar(max) NULL"),
            ("MfaJson", "nvarchar(max) NULL"),
            ("ForwardingRulesJson", "nvarchar(max) NULL"),
            ("InboxRulesJson", "nvarchar(max) NULL"),
        })
        {
            // column/definition come from the hardcoded array above, never user input.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"IF COL_LENGTH('CustomerUsers', '{column}') IS NULL ALTER TABLE CustomerUsers ADD {column} {definition}");
#pragma warning restore EF1002
        }
    }
}

// Pulls the current user list (plus licenses and, if permitted, mailbox
// usage) from a customer's tenant via Graph and replaces whatever was
// previously stored for them. A hard failure here (e.g. consent not granted
// yet) is recorded on the customer rather than thrown, so it can be retried
// later. Mailbox usage specifically needs a separate Reports.Read.All
// permission on top of the User.Read.All everything else uses — its own
// failure doesn't block users/licenses from still being collected, since
// tenants may not have granted it yet (see README).
static async Task CollectCustomerDataAsync(
    Customer customer,
    SpectraDbContext db,
    GraphAppClient graphClient,
    ExoPowerShellClient exoClient,
    DnsCheckClient dnsClient,
    SccPowerShellClient sccClient,
    ILogger logger,
    ConcurrentDictionary<int, SemaphoreSlim> collectionLocks)
{
    var gate = collectionLocks.GetOrAdd(customer.Id, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try
    {
        var graphUsers = await graphClient.ListUsersAsync(customer.TenantId);
        var token = await graphClient.GetAppTokenAsync(customer.TenantId);

        var licensesByUserId = new Dictionary<string, List<GraphLicenseDto>>();
        await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (u, ct) =>
        {
            try
            {
                var licenses = await graphClient.GetLicenseDetailsAsync(customer.TenantId, u.Id, token, ct);
                lock (licensesByUserId) licensesByUserId[u.Id] = licenses;
            }
            catch (Exception ex)
            {
                // Best-effort per user — one odd account (e.g. a resource
                // mailbox Graph won't return licenseDetails for) shouldn't
                // sink the rest of the tenant's collection.
                logger.LogWarning(ex, "Failed to get license details for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
            }
        });

        var warnings = new List<string>();

        Dictionary<string, GraphMailboxUsageDto> mailboxByUpn = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            mailboxByUpn = await graphClient.GetMailboxUsageByUpnAsync(customer.TenantId, token);

            // Real rows came back, but none of them match any actual user in
            // this tenant — the signature of Microsoft's default report
            // concealment (see Customer.MailboxDataConcealed), not a
            // permission problem. Only evaluate this when there's at least
            // one real user to check against, so a brand-new/empty tenant
            // doesn't get misdiagnosed.
            customer.MailboxDataConcealed = mailboxByUpn.Count > 0
                && graphUsers.Count > 0
                && !graphUsers.Any(u => mailboxByUpn.ContainsKey(u.UserPrincipalName));
            if (customer.MailboxDataConcealed)
            {
                warnings.Add("mailbox data unavailable — report identities are concealed (Microsoft 365 admin center → Settings → Org Settings → Services → Reports → check \"Display Concealed user, group, and site names in all reports\" → Save; takes a few minutes to take effect, then re-collect)");
            }
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "Reports.Read.All not yet effective for tenant {TenantId}", customer.TenantId);
            warnings.Add("mailbox data unavailable — Reports.Read.All isn't granted yet");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get mailbox usage for tenant {TenantId}", customer.TenantId);
            // Not a permission problem — don't tell the admin to re-consent when
            // that isn't the actual fix; show Graph's own explanation instead.
            warnings.Add($"mailbox data unavailable — {ex.Message}");
        }

        var mfaByUserId = new Dictionary<string, GraphMfaDto>();
        var mfaPermissionDenied = false;
        await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (u, ct) =>
        {
            try
            {
                var mfa = await graphClient.GetAuthenticationMethodsAsync(customer.TenantId, u.Id, token, ct);
                lock (mfaByUserId) mfaByUserId[u.Id] = mfa;
            }
            catch (GraphPermissionDeniedException)
            {
                mfaPermissionDenied = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get authentication methods for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
            }
        });
        if (mfaPermissionDenied)
        {
            warnings.Add("MFA data unavailable — UserAuthenticationMethod.Read.All isn't granted yet");
        }

        try
        {
            var policies = await graphClient.GetConditionalAccessPoliciesAsync(customer.TenantId, token);
            customer.ConditionalAccessPoliciesJson = JsonSerializer.Serialize(policies);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "Policy.Read.All not granted for tenant {TenantId}", customer.TenantId);
            warnings.Add("Conditional Access data unavailable — Policy.Read.All isn't granted yet");
            customer.ConditionalAccessPoliciesJson = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Conditional Access policies for tenant {TenantId}", customer.TenantId);
            warnings.Add($"Conditional Access data unavailable — {ex.Message}");
            customer.ConditionalAccessPoliciesJson = null;
        }

        try
        {
            var globalAdmins = await graphClient.GetGlobalAdministratorsAsync(customer.TenantId, token);
            customer.GlobalAdminsJson = JsonSerializer.Serialize(globalAdmins);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "RoleManagement.ReadWrite.Directory not granted for tenant {TenantId}", customer.TenantId);
            warnings.Add("Global Administrator data unavailable — RoleManagement.ReadWrite.Directory isn't granted yet");
            customer.GlobalAdminsJson = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Global Administrators for tenant {TenantId}", customer.TenantId);
            warnings.Add($"Global Administrator data unavailable — {ex.Message}");
            customer.GlobalAdminsJson = null;
        }

        try
        {
            customer.SecurityDefaultsEnabled = await graphClient.GetSecurityDefaultsEnabledAsync(customer.TenantId, token);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "Policy.Read.All not granted (Security Defaults) for tenant {TenantId}", customer.TenantId);
            customer.SecurityDefaultsEnabled = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Security Defaults status for tenant {TenantId}", customer.TenantId);
            customer.SecurityDefaultsEnabled = null;
        }

        // SPF/DMARC DNS checks — live public DNS lookups, not Graph or EXO,
        // so the only real failure mode here is "couldn't resolve the
        // verified domain list", not a missing permission.
        try
        {
            var verifiedDomains = await graphClient.GetVerifiedDomainsAsync(customer.TenantId, token);
            var domainChecks = await Task.WhenAll(verifiedDomains.Select(d => dnsClient.CheckDomainAsync(d)));
            customer.DnsRecordChecksJson = JsonSerializer.Serialize(domainChecks);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run SPF/DMARC DNS checks for tenant {TenantId}", customer.TenantId);
            warnings.Add($"DNS record checks unavailable — {ex.Message}");
            customer.DnsRecordChecksJson = null;
        }

        try
        {
            var secureScore = await graphClient.GetSecureScoreAsync(customer.TenantId, token);
            customer.SecureScoreJson = secureScore is null ? null : JsonSerializer.Serialize(secureScore);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "SecurityEvents.Read.All not granted for tenant {TenantId}", customer.TenantId);
            warnings.Add("Secure Score unavailable — SecurityEvents.Read.All isn't granted yet");
            customer.SecureScoreJson = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Secure Score for tenant {TenantId}", customer.TenantId);
            warnings.Add($"Secure Score unavailable — {ex.Message}");
            customer.SecureScoreJson = null;
        }

        try
        {
            var profiles = await graphClient.GetSecureScoreControlProfilesAsync(customer.TenantId, token);
            customer.SecureScoreControlProfilesJson = JsonSerializer.Serialize(profiles);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "SecurityEvents.Read.All not granted (control profiles) for tenant {TenantId}", customer.TenantId);
            customer.SecureScoreControlProfilesJson = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Secure Score control profiles for tenant {TenantId}", customer.TenantId);
            customer.SecureScoreControlProfilesJson = null;
        }

        var inboxRulesByUserId = new Dictionary<string, List<GraphInboxRuleDto>>();
        var inboxRulesPermissionDenied = false;
        await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (u, ct) =>
        {
            try
            {
                Console.WriteLine($"DEBUG inbox rules lookup: {u.UserPrincipalName} ({u.Id})");
                var rules = await graphClient.GetInboxRulesAsync(customer.TenantId, u.Id, token, ct);
                lock (inboxRulesByUserId) inboxRulesByUserId[u.Id] = rules;
            }
            catch (GraphPermissionDeniedException)
            {
                inboxRulesPermissionDenied = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get inbox rules for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
            }
        });
        if (inboxRulesPermissionDenied)
        {
            warnings.Add("inbox rule data unavailable — MailboxSettings.Read isn't granted yet");
        }

        // Exchange Online PowerShell data — a separate collection track from
        // everything above, needing its own access grant (Global Reader,
        // auto-assigned here) rather than Graph admin consent. Attempted on
        // every run until the role assignment succeeds, since it can lag
        // behind (or fail independently of) ConsentGranted.
        if (!customer.ExoRoleAssigned)
        {
            try
            {
                customer.ExoRoleAssigned = await graphClient.EnsureGlobalReaderRoleAssignedAsync(customer.TenantId, token);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "RoleManagement.ReadWrite.Directory not granted for tenant {TenantId}", customer.TenantId);
                warnings.Add("Exchange Online access setup unavailable — RoleManagement.ReadWrite.Directory isn't granted yet");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to assign Global Reader role for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Exchange Online role assignment failed — {ex.Message}");
            }
        }

        if (customer.ExoRoleAssigned)
        {
            string? organizationDomain = null;
            try
            {
                organizationDomain = await graphClient.GetInitialDomainAsync(customer.TenantId, token)
                    ?? throw new InvalidOperationException("Couldn't resolve the tenant's initial domain.");

                var exo = await exoClient.CollectAsync(organizationDomain);
                customer.ExoOrganizationConfigJson = exo.OrganizationConfig is null ? null : JsonSerializer.Serialize(exo.OrganizationConfig);
                customer.ExoAcceptedDomainsJson = exo.AcceptedDomains is null ? null : JsonSerializer.Serialize(exo.AcceptedDomains);
                customer.ExoAntiPhishPoliciesJson = exo.AntiPhishPolicies is null ? null : JsonSerializer.Serialize(exo.AntiPhishPolicies);
                customer.ExoSafeLinksPoliciesJson = exo.SafeLinksPolicies is null ? null : JsonSerializer.Serialize(exo.SafeLinksPolicies);
                customer.ExoSafeAttachmentPoliciesJson = exo.SafeAttachmentPolicies is null ? null : JsonSerializer.Serialize(exo.SafeAttachmentPolicies);
                customer.ExoHostedContentFilterPoliciesJson = exo.HostedContentFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedContentFilterPolicies);
                customer.ExoHostedOutboundSpamFilterPoliciesJson = exo.HostedOutboundSpamFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedOutboundSpamFilterPolicies);
                customer.ExoMalwareFilterPoliciesJson = exo.MalwareFilterPolicies is null ? null : JsonSerializer.Serialize(exo.MalwareFilterPolicies);
                customer.ExoDkimSigningConfigsJson = exo.DkimSigningConfigs is null ? null : JsonSerializer.Serialize(exo.DkimSigningConfigs);
                customer.ExoTransportRulesJson = exo.TransportRules is null ? null : JsonSerializer.Serialize(exo.TransportRules);
                customer.ExoSharingPoliciesJson = exo.SharingPolicies is null ? null : JsonSerializer.Serialize(exo.SharingPolicies);
                customer.ExoHostedConnectionFilterPoliciesJson = exo.HostedConnectionFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedConnectionFilterPolicies);
                customer.ExoAdminAuditLogConfigJson = exo.AdminAuditLogConfig is null ? null : JsonSerializer.Serialize(exo.AdminAuditLogConfig);
                customer.ExoAtpPolicyForO365Json = exo.AtpPolicyForO365 is null ? null : JsonSerializer.Serialize(exo.AtpPolicyForO365);
                customer.ExoRemoteDomainsJson = exo.RemoteDomains is null ? null : JsonSerializer.Serialize(exo.RemoteDomains);
                customer.ExoMailboxAuditBypassJson = exo.MailboxAuditBypass is null ? null : JsonSerializer.Serialize(exo.MailboxAuditBypass);
                customer.ExoMailboxForwardingJson = exo.MailboxForwarding is null ? null : JsonSerializer.Serialize(exo.MailboxForwarding);
                customer.ExoLastCollectedAt = DateTimeOffset.UtcNow;
                customer.ExoLastError = null;
            }
            catch (ExoAccessException ex)
            {
                // Deliberately NOT nulling the 11 Exo*Json columns here (see
                // the doc comment on Customer.ExoLastError) — EXO PowerShell
                // is meaningfully flakier than a Graph HTTP call (external
                // process, cert auth, role-propagation delay that can take
                // ~15 minutes even right after a successful assignment
                // above), and wiping previously-good check results on a
                // transient failure would make a working integration look
                // broken instead of just stale.
                logger.LogWarning(ex, "Exchange Online access not yet effective for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Exchange Online checks unavailable — {ex.Message}");
                customer.ExoLastError = ex.Message;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exchange Online collection failed for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Exchange Online checks failed — {ex.Message}");
                customer.ExoLastError = ex.Message;
            }

            // A separate PowerShell session (Connect-IPPSSession) from the EXO
            // one above — attempted independently as long as the tenant domain
            // resolved, regardless of whether the EXO collection itself
            // succeeded, since a failure in one session shouldn't block the
            // other from running.
            if (organizationDomain is not null)
            {
                try
                {
                    var scc = await sccClient.CollectAsync(organizationDomain);
                    customer.SccDlpPoliciesJson = scc.DlpPolicies is null ? null : JsonSerializer.Serialize(scc.DlpPolicies);
                    customer.SccRetentionPoliciesJson = scc.RetentionPolicies is null ? null : JsonSerializer.Serialize(scc.RetentionPolicies);
                    customer.SccAlertPoliciesJson = scc.AlertPolicies is null ? null : JsonSerializer.Serialize(scc.AlertPolicies);
                    customer.SccLastCollectedAt = DateTimeOffset.UtcNow;
                    customer.SccLastError = null;
                }
                catch (SccAccessException ex)
                {
                    logger.LogWarning(ex, "Security & Compliance access not yet effective for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Security & Compliance checks unavailable — {ex.Message}");
                    customer.SccLastError = ex.Message;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Security & Compliance collection failed for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Security & Compliance checks failed — {ex.Message}");
                    customer.SccLastError = ex.Message;
                }
            }
        }

        var existing = await db.CustomerUsers.Where(u => u.CustomerId == customer.Id).ToListAsync();
        db.CustomerUsers.RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        db.CustomerUsers.AddRange(graphUsers.Select(u =>
        {
            var mailbox = mailboxByUpn.GetValueOrDefault(u.UserPrincipalName);
            var mfa = mfaByUserId.GetValueOrDefault(u.Id);
            return new CustomerUser
            {
                CustomerId = customer.Id,
                GraphUserId = u.Id,
                DisplayName = u.DisplayName,
                Mail = u.Mail,
                UserPrincipalName = u.UserPrincipalName,
                JobTitle = u.JobTitle,
                Department = u.Department,
                OfficeLocation = u.OfficeLocation,
                AccountEnabled = u.AccountEnabled,
                CreatedDateTime = u.CreatedDateTime,
                MailboxSizeBytes = mailbox?.SizeBytes,
                MailboxItemCount = mailbox?.ItemCount,
                HasArchiveMailbox = mailbox?.HasArchive,
                LicensesJson = licensesByUserId.TryGetValue(u.Id, out var licenses) ? SerializeLicenses(licenses) : null,
                MfaJson = mfa is null ? null : JsonSerializer.Serialize(mfa),
                InboxRulesJson = inboxRulesByUserId.TryGetValue(u.Id, out var inboxRules) ? JsonSerializer.Serialize(inboxRules) : null,
                ForwardingRulesJson = inboxRulesByUserId.TryGetValue(u.Id, out var allRules)
                    ? JsonSerializer.Serialize(allRules
                        .Where(r => r.ForwardsTo.Count > 0)
                        .Select(r => new GraphForwardingRuleDto(r.Name, r.Enabled, r.ForwardsTo)))
                    : null,
                SyncedAt = now,
            };
        }));

        customer.ConsentGranted = true;
        customer.LastSyncedAt = now;
        customer.LastSyncError = warnings.Count == 0
            ? null
            : $"Users and licenses synced, but some data is unavailable: {string.Join("; ", warnings)}.";
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to collect data for customer {CustomerId} ({TenantId})", customer.Id, customer.TenantId);
        customer.LastSyncError = ex.Message;
    }

    try
    {
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        // The per-customer lock above should make this unreachable in
        // practice — kept as a backstop so any other save failure still
        // surfaces as a normal API error instead of an unhandled 500.
        logger.LogError(ex, "Failed to save collected data for customer {CustomerId}", customer.Id);
    }
    finally
    {
        gate.Release();
    }
}

static string SerializeLicenses(List<GraphLicenseDto> licenses) =>
    JsonSerializer.Serialize(licenses.Select(l => new { l.SkuId, l.SkuPartNumber }));

static List<object> DeserializeLicenses(string? licensesJson)
{
    if (string.IsNullOrEmpty(licensesJson))
    {
        return [];
    }
    try
    {
        var raw = JsonSerializer.Deserialize<List<StoredLicense>>(licensesJson) ?? [];
        return raw.Select(l => (object)new { l.SkuId, l.SkuPartNumber, DisplayName = LicenseSkuNames.DisplayName(l.SkuPartNumber) }).ToList();
    }
    catch (JsonException)
    {
        return [];
    }
}

static GraphMfaDto? DeserializeMfa(string? mfaJson)
{
    if (string.IsNullOrEmpty(mfaJson))
    {
        return null;
    }
    try
    {
        return JsonSerializer.Deserialize<GraphMfaDto>(mfaJson);
    }
    catch (JsonException)
    {
        return null;
    }
}

static List<GraphForwardingRuleDto> DeserializeForwardingRules(string? rulesJson)
{
    if (string.IsNullOrEmpty(rulesJson))
    {
        return [];
    }
    try
    {
        return JsonSerializer.Deserialize<List<GraphForwardingRuleDto>>(rulesJson) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }
}

static List<GraphInboxRuleDto> DeserializeInboxRules(string? rulesJson)
{
    if (string.IsNullOrEmpty(rulesJson))
    {
        return [];
    }
    try
    {
        return JsonSerializer.Deserialize<List<GraphInboxRuleDto>>(rulesJson) ?? [];
    }
    catch (JsonException)
    {
        return [];
    }
}

record StoredLicense(string SkuId, string SkuPartNumber);

record UpdateSettingsRequest(string? AdminGroupId, string? AdminGroupDisplayName);
record CreateCustomerRequest(string? Name, string? TenantId);
record DatabaseConnectionRequest(string DatabaseType, string Host, int Port, string DatabaseName, string Username, string Password);
