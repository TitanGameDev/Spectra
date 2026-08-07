using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

// Free for individuals/businesses under $1M USD annual gross revenue (see
// the README's PDF report section) — set once at startup, no license key
// needed for the Community tier.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Tenant/ClientId come from AzureAdConfig in the database, not static config —
// but AddMicrosoftIdentityWebApi needs them now, before the DI container (and
// therefore the DB) exists. AzureAdBootstrapStore reads a small file kept in
// sync with the DB row for exactly this purpose; see its doc comment. When
// unconfigured (fresh install, nobody's set up Azure AD yet), these placeholder
// values build a syntactically valid authority/audience that simply validates
// no real token — fine, since nothing reaches this far before
// /api/public/auth-config reports configured:true and the frontend leaves the
// setup wizard.
var azureAdBootstrap = AzureAdBootstrapStore.Load(builder.Environment.ContentRootPath);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        _ => { },
        (MicrosoftIdentityOptions options) =>
        {
            options.Instance = "https://login.microsoftonline.com/";
            options.TenantId = azureAdBootstrap.TenantId ?? "common";
            options.ClientId = azureAdBootstrap.BackendClientId ?? "00000000-0000-0000-0000-000000000000";
        });

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
builder.Services.AddKeyedSingleton(
    "AzureAdConfig",
    (sp, _) => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Spectra.AzureAdConfig"));

builder.Services.AddSingleton<IActiveDatabaseProvider, ActiveDatabaseProvider>();
builder.Services.AddSingleton<DatabaseHealth>();
builder.Services.AddSingleton<IActiveAzureAdConfigProvider, ActiveAzureAdConfigProvider>();

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
builder.Services.AddHttpClient<AzureResourceClient>();

// Shells out to pwsh for Exchange Online PowerShell collection — no HttpClient
// needed, see ExoPowerShellClient.cs.
builder.Services.AddSingleton<ExoPowerShellClient>();

// Shells out to pwsh for Security & Compliance PowerShell collection — a
// sibling to ExoPowerShellClient using the same cert/role, see SccPowerShellClient.cs.
builder.Services.AddSingleton<SccPowerShellClient>();

// Direct DNS TXT-record lookups for SPF/DMARC checks — no HttpClient, no
// auth, see DnsCheckClient.cs.
builder.Services.AddSingleton<DnsCheckClient>();

// Shared per-customer collection lock table (singleton, so it's the same
// instance regardless of which scope/request resolves it) and the scoped
// service that actually runs a collection — see CollectionLockRegistry.cs
// and CustomerCollectionService.cs. One implementation, called by both the
// manual "Collect data" endpoints below and CustomerSyncBackgroundService's
// scheduled runs.
builder.Services.AddSingleton<CollectionLockRegistry>();
builder.Services.AddScoped<CustomerCollectionService>();

// Runs CustomerCollectionService for every customer on a timer — see
// CustomerSyncBackgroundService.cs. Configure via Sync:IntervalHours
// (Sync__IntervalHours in .env); 0 disables it.
builder.Services.AddHostedService<CustomerSyncBackgroundService>();

// Settings -> Updates panel — see AppUpdateService.cs. Inert (IsConfigured
// false) unless Update:DataDir is set, which only deploy/install.sh sets.
builder.Services.AddSingleton<AppUpdateService>();

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

    try
    {
        var azureAdProvider = scope.ServiceProvider.GetRequiredService<IActiveAzureAdConfigProvider>();
        var azureAdProtector = scope.ServiceProvider.GetRequiredKeyedService<IDataProtector>("AzureAdConfig");
        var azureAdConfig = await db.AzureAdConfigs.AsNoTracking().SingleOrDefaultAsync();
        if (azureAdConfig is { IsConfigured: true })
        {
            var secret = azureAdProtector.Unprotect(azureAdConfig.EncryptedBackendClientSecret);
            azureAdProvider.Update(azureAdConfig.TenantId, azureAdConfig.FrontendClientId, azureAdConfig.BackendClientId, secret, azureAdConfig.ApiScope);
        }
    }
    catch (Exception ex)
    {
        // Same degrade-gracefully convention as the database-health block above —
        // an unreadable/undecryptable row just means "not configured yet" (the
        // setup wizard stays reachable) rather than a crash at startup.
        app.Logger.LogWarning(ex, "Couldn't load Azure AD config at startup — treating as unconfigured");
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
        Aliases = DeserializeExo<List<UserAliasDto>>(u.AliasesJson) ?? [],
        ForwardingRules = DeserializeForwardingRules(u.ForwardingRulesJson),
        InboxRules = DeserializeInboxRules(u.InboxRulesJson),
    }));
}).RequireAuthorization();

// A branded, downloadable snapshot of the Users tab — Directory, Licenses,
// and (when available) Mailboxes — reusing the exact same stored data
// /users above already serves, styled the same way as /report's security
// snapshot (see UserReportPdfGenerator).
app.MapGet("/api/customers/{id:int}/users-report", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var allUsers = await db.CustomerUsers
        .Where(u => u.CustomerId == id)
        .OrderBy(u => u.DisplayName)
        .ToListAsync();

    // Disabled accounts are noise in a client-facing report — someone who
    // left the org isn't part of "the people at this customer" anymore, and
    // their stale MFA/license state doesn't tell the client anything useful.
    var users = allUsers.Where(u => u.AccountEnabled).ToList();
    var disabledExcludedCount = allUsers.Count - users.Count;

    var rows = users.Select(u => new UserReportUserRow(
        DisplayName: u.DisplayName ?? u.UserPrincipalName,
        Email: u.Mail ?? u.UserPrincipalName,
        JobTitle: u.JobTitle,
        Department: u.Department,
        MfaRegistered: DeserializeMfa(u.MfaJson)?.IsMfaRegistered == true,
        LicenseNames: string.IsNullOrEmpty(u.LicensesJson)
            ? []
            : (JsonSerializer.Deserialize<List<StoredLicense>>(u.LicensesJson) ?? [])
                .Select(l => LicenseSkuNames.DisplayName(l.SkuPartNumber))
                .ToList(),
        MailboxSizeBytes: u.MailboxSizeBytes,
        MailboxItemCount: u.MailboxItemCount)).ToList();

    var mailboxDataAvailable = !customer.MailboxDataConcealed
        && users.Any(u => u.MailboxSizeBytes is not null || u.MailboxItemCount is not null);

    var report = new UserReportData(
        CustomerName: customer.Name,
        GeneratedAt: DateTimeOffset.UtcNow,
        MailboxDataAvailable: mailboxDataAvailable,
        DisabledExcludedCount: disabledExcludedCount,
        Users: rows);

    var pdfBytes = UserReportPdfGenerator.Generate(report);
    var fileName = $"{customer.Name} User Report {DateTime.UtcNow:yyyy-MM-dd}.pdf".Replace('/', '-');
    return Results.File(pdfBytes, "application/pdf", fileName);
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
    var mailboxPermissions = DeserializeExo<List<ExoMailboxPermissionDto>>(customer.ExoMailboxPermissionsJson);
    var recipientPermissions = DeserializeExo<List<ExoRecipientPermissionDto>>(customer.ExoRecipientPermissionsJson);

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
        // show mailbox forwarding (Forwarding Rules tab), mail flow rules
        // (Mail Flow Rules tab), and delegate access (Mailbox Access tab) as
        // browsable tables, not just baked into a single check each.
        MailboxForwarding = mailboxForwarding ?? [],
        TransportRules = transportRules ?? [],
        MailboxPermissions = mailboxPermissions ?? [],
        RecipientPermissions = recipientPermissions ?? [],
    });
}).RequireAuthorization();

// Azure Resource Manager + Entra Apps data — two independent tracks with
// their own setup gates (RBAC role assignment vs Application.Read.All
// Graph consent, see Customer.AzureSubscriptionsJson/EntraAppRegistrationsJson),
// so both ride together on one endpoint the same way the tabs sharing it do.
app.MapGet("/api/customers/{id:int}/azure", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        customer.AzureLastCollectedAt,
        customer.AzureLastError,
        Subscriptions = DeserializeExo<List<AzureSubscriptionDto>>(customer.AzureSubscriptionsJson) ?? [],
        VirtualMachines = DeserializeExo<List<AzureVirtualMachineDto>>(customer.AzureVirtualMachinesJson) ?? [],
        AppServices = DeserializeExo<List<AzureAppServiceDto>>(customer.AzureAppServicesJson) ?? [],
        Reservations = DeserializeExo<List<AzureReservationDto>>(customer.AzureReservationsJson) ?? [],
        EntraAppRegistrations = DeserializeExo<List<GraphApplicationDto>>(customer.EntraAppRegistrationsJson) ?? [],
        EntraServicePrincipals = DeserializeExo<List<GraphServicePrincipalDto>>(customer.EntraServicePrincipalsJson) ?? [],
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

// A branded, downloadable snapshot of everything the Security tab shows —
// styled to match the app's own dark theme rather than a generic report
// template (see SecurityReportPdfGenerator). Re-evaluates the same stored
// data the 4 check endpoints above already serve, rather than introducing a
// separate collection or storage path — a customer's report is always
// exactly what their dashboard already shows, nothing more.
app.MapGet("/api/customers/{id:int}/report", async (int id, SpectraDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var secureScore = string.IsNullOrEmpty(customer.SecureScoreJson)
        ? null
        : JsonSerializer.Deserialize<GraphSecureScoreDto>(customer.SecureScoreJson);

    var caPolicies = string.IsNullOrEmpty(customer.ConditionalAccessPoliciesJson)
        ? []
        : JsonSerializer.Deserialize<List<GraphConditionalAccessPolicyDto>>(customer.ConditionalAccessPoliciesJson) ?? [];

    // Disabled accounts are excluded — same reasoning as the User Report:
    // a departed user's stale MFA state shouldn't count against or for the
    // tenant's actual MFA coverage in a client-facing report.
    var mfaRows = await db.CustomerUsers
        .Where(u => u.CustomerId == id && u.AccountEnabled)
        .Select(u => u.MfaJson)
        .ToListAsync();
    var mfaRegisteredCount = mfaRows.Count(json => DeserializeMfa(json)?.IsMfaRegistered == true);

    var emailSecurityChecks = customer.ExoLastCollectedAt is null
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
            DeserializeExo<List<ExoTransportRuleDto>>(customer.ExoTransportRulesJson),
            DeserializeExo<List<ExoSharingPolicyDto>>(customer.ExoSharingPoliciesJson),
            DeserializeExo<List<ExoHostedConnectionFilterPolicyDto>>(customer.ExoHostedConnectionFilterPoliciesJson),
            DeserializeExo<ExoAdminAuditLogConfigDto>(customer.ExoAdminAuditLogConfigJson),
            DeserializeExo<ExoAtpPolicyForO365Dto>(customer.ExoAtpPolicyForO365Json),
            DeserializeExo<List<ExoRemoteDomainDto>>(customer.ExoRemoteDomainsJson),
            DeserializeExo<List<ExoMailboxAuditBypassDto>>(customer.ExoMailboxAuditBypassJson),
            DeserializeExo<List<ExoMailboxForwardingDto>>(customer.ExoMailboxForwardingJson));

    var identityChecks = DirectoryCheckEvaluator.Evaluate(
        DeserializeExo<List<GraphUserDto>>(customer.GlobalAdminsJson),
        customer.SecurityDefaultsEnabled,
        caPolicies,
        (await db.CustomerUsers.Where(u => u.CustomerId == id).Select(u => new { u.GraphUserId, u.MfaJson }).ToListAsync())
            .ToDictionary(u => u.GraphUserId, u => DeserializeMfa(u.MfaJson)?.IsMfaRegistered));

    var domainChecks = DnsCheckEvaluator.Evaluate(DeserializeExo<List<DnsRecordCheckDto>>(customer.DnsRecordChecksJson));

    var complianceChecks = customer.SccLastCollectedAt is null
        ? []
        : SccCheckEvaluator.Evaluate(
            DeserializeExo<List<SccDlpPolicyDto>>(customer.SccDlpPoliciesJson),
            DeserializeExo<List<SccRetentionPolicyDto>>(customer.SccRetentionPoliciesJson),
            DeserializeExo<List<SccAlertPolicyDto>>(customer.SccAlertPoliciesJson));

    var report = new SecurityReportData(
        CustomerName: customer.Name,
        GeneratedAt: DateTimeOffset.UtcNow,
        SecureScoreCurrent: secureScore is null ? null : (int)Math.Round(secureScore.CurrentScore),
        SecureScoreMax: secureScore is null ? null : (int)Math.Round(secureScore.MaxScore),
        MfaRegisteredCount: mfaRegisteredCount,
        TotalUserCount: mfaRows.Count,
        ConditionalAccessEnabledCount: caPolicies.Count(p => p.State == "enabled"),
        ConditionalAccessTotalCount: caPolicies.Count,
        EmailSecurityChecks: emailSecurityChecks,
        IdentityChecks: identityChecks,
        DomainChecks: domainChecks,
        ComplianceChecks: complianceChecks);

    var pdfBytes = SecurityReportPdfGenerator.Generate(report);
    var fileName = $"{customer.Name} Security Report {DateTime.UtcNow:yyyy-MM-dd}.pdf".Replace('/', '-');
    return Results.File(pdfBytes, "application/pdf", fileName);
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

app.MapGet("/api/customers/{id:int}/consent-url", async (int id, SpectraDbContext db, IActiveAzureAdConfigProvider azureAdConfig, IConfiguration configuration) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var clientId = azureAdConfig.BackendClientId;
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

// Azure RBAC is a completely separate trust model from Entra admin consent
// above — there's no Graph permission that lets Spectra grant itself a role
// assignment, since ARM requires the *caller* to already have
// Microsoft.Authorization/roleAssignments/write on the target scope. This
// can't be automated away, so the next best thing: a ready-to-run az CLI
// command scoped at the tenant's root management group (id == the tenant
// id itself, present for every tenant) rather than one subscription at a
// time — a single run covers every current *and future* subscription in
// the tenant, since RBAC inherits down the management group hierarchy.
app.MapGet("/api/customers/{id:int}/azure-role-command", async (int id, SpectraDbContext db, IActiveAzureAdConfigProvider azureAdConfig) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var clientId = azureAdConfig.BackendClientId ?? "";
    var command =
        $"az role assignment create --assignee \"{clientId}\" --assignee-principal-type ServicePrincipal " +
        $"--role \"Reader\" --scope \"/providers/Microsoft.Management/managementGroups/{customer.TenantId}\"";

    return Results.Ok(new { command });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/customers", async (
    CreateCustomerRequest request,
    ClaimsPrincipal user,
    SpectraDbContext db,
    CustomerCollectionService collectionService) =>
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
    // either way and collection can be retried from Settings, or picked up
    // automatically by the next CustomerSyncBackgroundService run.
    await collectionService.CollectAsync(customer);

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
    CustomerCollectionService collectionService) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    await collectionService.CollectAsync(customer);

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
        var azureAdConfigs = await db.AzureAdConfigs.AsNoTracking().ToListAsync();

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
                ExoMailboxPermissionsJson = c.ExoMailboxPermissionsJson,
                ExoRecipientPermissionsJson = c.ExoRecipientPermissionsJson,
                SccLastCollectedAt = c.SccLastCollectedAt,
                SccLastError = c.SccLastError,
                SccDlpPoliciesJson = c.SccDlpPoliciesJson,
                SccRetentionPoliciesJson = c.SccRetentionPoliciesJson,
                SccAlertPoliciesJson = c.SccAlertPoliciesJson,
                AzureSubscriptionsJson = c.AzureSubscriptionsJson,
                AzureVirtualMachinesJson = c.AzureVirtualMachinesJson,
                AzureAppServicesJson = c.AzureAppServicesJson,
                AzureLastCollectedAt = c.AzureLastCollectedAt,
                AzureLastError = c.AzureLastError,
                AzureReservationsJson = c.AzureReservationsJson,
                EntraAppRegistrationsJson = c.EntraAppRegistrationsJson,
                EntraServicePrincipalsJson = c.EntraServicePrincipalsJson,
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
                    AliasesJson = u.AliasesJson,
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
        if (!await targetDb.AzureAdConfigs.AnyAsync())
        {
            targetDb.AzureAdConfigs.AddRange(azureAdConfigs.Select(a => new AzureAdConfig
            {
                TenantId = a.TenantId,
                FrontendClientId = a.FrontendClientId,
                BackendClientId = a.BackendClientId,
                ApiScope = a.ApiScope,
                EncryptedBackendClientSecret = a.EncryptedBackendClientSecret,
                IsConfigured = a.IsConfigured,
                UpdatedAt = a.UpdatedAt,
                UpdatedByEmail = a.UpdatedByEmail,
            }));
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

// Fetched by the frontend before it ever constructs an MSAL PublicClientApplication
// — this is what lets a fresh install render a setup wizard instead of a login
// button, and lets existing installs redirect their MSAL config through the
// database instead of a build-time env var. Never returns the backend client ID
// or secret — only what the frontend needs to talk to the frontend app registration.
app.MapGet("/api/public/auth-config", async (SpectraDbContext db, ILogger<Program> logger) =>
{
    try
    {
        var config = await db.AzureAdConfigs.AsNoTracking().SingleOrDefaultAsync();
        return Results.Ok(new
        {
            configured = config?.IsConfigured ?? false,
            clientId = config?.FrontendClientId,
            tenantId = config?.TenantId,
            apiScope = config?.ApiScope,
        });
    }
    catch (Exception ex)
    {
        // DB unreachable/unmigrated — degrade to "not configured" rather than 500,
        // same convention as DatabaseHealth: the frontend must always be able to
        // render *something* (the setup wizard) instead of a blank crash screen.
        logger.LogWarning(ex, "Couldn't read Azure AD config for /api/public/auth-config");
        return Results.Ok(new { configured = false, clientId = (string?)null, tenantId = (string?)null, apiScope = (string?)null });
    }
}).AllowAnonymous();

// One-time setup — either pasted into the SetupWizard by hand, or POSTed
// directly by deploy/setup-azure-ad.sh using the token install.sh printed to
// /etc/spectra/setup-token.txt. Rejects once AzureAdConfig.IsConfigured is
// already true, which is also what makes the setup token permanently inert
// after first use — no separate expiry/deletion needed.
app.MapPost("/api/setup/azure-ad", async (
    HttpContext httpContext,
    AzureAdConfigRequest request,
    SpectraDbContext db,
    [FromKeyedServices("AzureAdConfig")] IDataProtector protector,
    IActiveAzureAdConfigProvider azureAdProvider,
    AppUpdateService updateService,
    IWebHostEnvironment env,
    IConfiguration configuration,
    ILogger<Program> logger) =>
{
    var existing = await db.AzureAdConfigs.SingleOrDefaultAsync();
    if (existing is { IsConfigured: true })
    {
        return Results.Conflict(new { error = "Azure AD is already configured. Sign in and use Settings → Authentication to change it." });
    }

    var configuredToken = configuration["Setup:Token"];
    if (string.IsNullOrEmpty(configuredToken))
    {
        if (!env.IsDevelopment())
        {
            return Results.Problem("Setup:Token is not configured on this server.", statusCode: 500);
        }
        logger.LogWarning("Setup:Token not configured — allowing unauthenticated setup because this is Development.");
    }
    else if (!ConstantTimeTokenEquals(request.SetupToken ?? "", configuredToken))
    {
        return Results.Json(new { error = "Invalid setup token." }, statusCode: 401);
    }

    return await SaveAzureAdConfigAsync(
        httpContext, db, protector, azureAdProvider, updateService, env, logger,
        request.TenantId, request.FrontendClientId, request.BackendClientId, request.BackendClientSecret, request.ApiScope,
        updatedByEmail: "(initial setup)");
}).AllowAnonymous();

// Settings -> Authentication panel — same save logic as the setup endpoint
// above, reachable after login for later edits (rotating a secret, fixing a
// typo). A blank BackendClientSecret means "keep the existing one" — a
// deliberate divergence from the Database panel (which always requires the
// password), since Azure AD values are plausibly revisited more often without
// wanting to re-paste a secret that isn't changing.
app.MapGet("/api/settings/azure-ad", async (SpectraDbContext db) =>
{
    var config = await db.AzureAdConfigs.SingleOrDefaultAsync();
    return Results.Ok(new
    {
        configured = config?.IsConfigured ?? false,
        tenantId = config?.TenantId,
        frontendClientId = config?.FrontendClientId,
        backendClientId = config?.BackendClientId,
        apiScope = config?.ApiScope,
        hasSecret = config is not null,
        updatedAt = config?.UpdatedAt,
        updatedByEmail = config?.UpdatedByEmail,
    });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/settings/azure-ad", async (
    HttpContext httpContext,
    AzureAdConfigRequest request,
    ClaimsPrincipal user,
    SpectraDbContext db,
    [FromKeyedServices("AzureAdConfig")] IDataProtector protector,
    IActiveAzureAdConfigProvider azureAdProvider,
    AppUpdateService updateService,
    IWebHostEnvironment env,
    ILogger<Program> logger) =>
{
    var email = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "";
    return await SaveAzureAdConfigAsync(
        httpContext, db, protector, azureAdProvider, updateService, env, logger,
        request.TenantId, request.FrontendClientId, request.BackendClientId, request.BackendClientSecret, request.ApiScope,
        updatedByEmail: email);
}).RequireAuthorization("AdminOnly");

// Settings -> Updates panel. See AppUpdateService.cs for the full flow —
// this endpoint never performs an update itself, only reports what a
// root-owned process (deploy/install.sh / deploy/update.sh) has recorded.
app.MapGet("/api/settings/update-status", async (AppUpdateService updateService) =>
{
    var currentVersion = updateService.ReadCurrentVersion();
    var status = updateService.ReadRunStatus();

    string? latestCommit = null;
    bool? updateAvailable = null;
    if (updateService.IsConfigured && currentVersion is not null)
    {
        latestCommit = await updateService.GetLatestRemoteCommitAsync();
        if (latestCommit is not null)
        {
            updateAvailable = !string.Equals(latestCommit, currentVersion.Commit, StringComparison.OrdinalIgnoreCase);
        }
    }

    return Results.Ok(new { currentVersion, updateAvailable, latestCommit, status });
}).RequireAuthorization("AdminOnly");

// Never performs the update inline — only writes the request flag a
// separate systemd .path unit watches for (see AppUpdateService.RequestUpdate
// and the README). The backend process never gains any elevated privilege.
app.MapPost("/api/settings/update", (ClaimsPrincipal user, AppUpdateService updateService) =>
{
    var email = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "";
    var (queued, error) = updateService.RequestUpdate(email);
    if (!queued)
    {
        return Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);
    }
    return Results.Json(new { queued = true }, statusCode: StatusCodes.Status202Accepted);
}).RequireAuthorization("AdminOnly");

app.Run();

static bool IsValidSqlIdentifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$");

// Hashes both sides before comparing so a mismatched length doesn't leak via
// early-exit timing the way a raw CryptographicOperations.FixedTimeEquals over
// the original strings could.
static bool ConstantTimeTokenEquals(string provided, string configured) =>
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
        SHA256.HashData(Encoding.UTF8.GetBytes(configured)));

// Shared by /api/setup/azure-ad (one-time, unauthenticated) and
// /api/settings/azure-ad (AdminOnly, for later edits) — same validation and
// save logic either way. Always restarts the backend afterward: see
// AppUpdateService.RequestRestart's doc comment for why this can't be a live,
// no-restart change the way Database settings are.
static async Task<IResult> SaveAzureAdConfigAsync(
    HttpContext httpContext,
    SpectraDbContext db,
    IDataProtector protector,
    IActiveAzureAdConfigProvider azureAdProvider,
    AppUpdateService updateService,
    IWebHostEnvironment env,
    ILogger<Program> logger,
    string? tenantId,
    string? frontendClientId,
    string? backendClientId,
    string? backendClientSecret,
    string? apiScope,
    string updatedByEmail)
{
    if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId, out _))
    {
        return Results.BadRequest(new { error = "TenantId must be a valid GUID." });
    }
    if (string.IsNullOrWhiteSpace(frontendClientId) || !Guid.TryParse(frontendClientId, out _))
    {
        return Results.BadRequest(new { error = "FrontendClientId must be a valid GUID." });
    }
    if (string.IsNullOrWhiteSpace(backendClientId) || !Guid.TryParse(backendClientId, out _))
    {
        return Results.BadRequest(new { error = "BackendClientId must be a valid GUID." });
    }
    if (string.IsNullOrWhiteSpace(apiScope) || !apiScope.StartsWith("api://", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "ApiScope must look like api://<backend-client-id>/access_as_user." });
    }

    var config = await db.AzureAdConfigs.SingleOrDefaultAsync();
    var keepingExistingSecret = string.IsNullOrEmpty(backendClientSecret) && config is not null;
    if (!keepingExistingSecret && string.IsNullOrWhiteSpace(backendClientSecret))
    {
        return Results.BadRequest(new { error = "BackendClientSecret is required." });
    }

    if (config is null)
    {
        config = new AzureAdConfig
        {
            TenantId = "",
            FrontendClientId = "",
            BackendClientId = "",
            ApiScope = "",
            EncryptedBackendClientSecret = "",
            UpdatedByEmail = "",
        };
        db.AzureAdConfigs.Add(config);
    }

    config.TenantId = tenantId;
    config.FrontendClientId = frontendClientId;
    config.BackendClientId = backendClientId;
    config.ApiScope = apiScope;
    if (!keepingExistingSecret)
    {
        config.EncryptedBackendClientSecret = protector.Protect(backendClientSecret!);
    }
    config.IsConfigured = true;
    config.UpdatedAt = DateTimeOffset.UtcNow;
    config.UpdatedByEmail = updatedByEmail;
    await db.SaveChangesAsync();

    var plaintextSecret = keepingExistingSecret ? protector.Unprotect(config.EncryptedBackendClientSecret) : backendClientSecret!;
    azureAdProvider.Update(tenantId, frontendClientId, backendClientId, plaintextSecret, apiScope);
    AzureAdBootstrapStore.Save(env.ContentRootPath, tenantId, backendClientId);

    // spectra-restarter.path reacts almost instantly once the restart flag
    // file exists — requesting it inline here raced with this very HTTP
    // response still being sent, killing the connection outright (504/reset)
    // instead of delivering a clean 200. Deferring to OnCompleted guarantees
    // the response has actually gone out before the process can be restarted.
    // The "not configured" branch is cheap (no I/O, returns immediately), so
    // it's safe to call synchronously to get an accurate message.
    bool restartQueued;
    string? restartError;
    if (updateService.IsConfigured)
    {
        restartQueued = true;
        restartError = null;
        httpContext.Response.OnCompleted(() =>
        {
            var (queued, error) = updateService.RequestRestart();
            if (!queued)
            {
                logger.LogWarning("Deferred restart request failed after Azure AD config save: {Error}", error);
            }
            return Task.CompletedTask;
        });
    }
    else
    {
        (restartQueued, restartError) = updateService.RequestRestart();
    }

    return Results.Ok(new { success = true, restartRequired = true, restartQueued, restartError });
}

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
            ("ExoMailboxPermissionsJson", "LONGTEXT NULL"),
            ("ExoRecipientPermissionsJson", "LONGTEXT NULL"),
            ("MailboxDataConcealed", "TINYINT(1) NOT NULL DEFAULT 0"),
            ("SccLastCollectedAt", "DATETIME(6) NULL"),
            ("SccLastError", "LONGTEXT NULL"),
            ("SccDlpPoliciesJson", "LONGTEXT NULL"),
            ("SccRetentionPoliciesJson", "LONGTEXT NULL"),
            ("SccAlertPoliciesJson", "LONGTEXT NULL"),
            ("AzureSubscriptionsJson", "LONGTEXT NULL"),
            ("AzureVirtualMachinesJson", "LONGTEXT NULL"),
            ("AzureAppServicesJson", "LONGTEXT NULL"),
            ("AzureLastCollectedAt", "DATETIME(6) NULL"),
            ("AzureLastError", "LONGTEXT NULL"),
            ("AzureReservationsJson", "LONGTEXT NULL"),
            ("EntraAppRegistrationsJson", "LONGTEXT NULL"),
            ("EntraServicePrincipalsJson", "LONGTEXT NULL"),
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
                AliasesJson LONGTEXT NULL,
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
            ("AliasesJson", "LONGTEXT NULL"),
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
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AzureAdConfigs (
                Id INT NOT NULL AUTO_INCREMENT,
                TenantId LONGTEXT NOT NULL,
                FrontendClientId LONGTEXT NOT NULL,
                BackendClientId LONGTEXT NOT NULL,
                ApiScope LONGTEXT NOT NULL,
                EncryptedBackendClientSecret LONGTEXT NOT NULL,
                IsConfigured TINYINT(1) NOT NULL,
                UpdatedAt DATETIME(6) NOT NULL,
                UpdatedByEmail LONGTEXT NOT NULL,
                PRIMARY KEY (Id)
            ) CHARACTER SET utf8mb4
            """);
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
            ("ExoMailboxPermissionsJson", "nvarchar(max) NULL"),
            ("ExoRecipientPermissionsJson", "nvarchar(max) NULL"),
            ("MailboxDataConcealed", "bit NOT NULL DEFAULT 0"),
            ("SccLastCollectedAt", "datetimeoffset NULL"),
            ("SccLastError", "nvarchar(max) NULL"),
            ("SccDlpPoliciesJson", "nvarchar(max) NULL"),
            ("SccRetentionPoliciesJson", "nvarchar(max) NULL"),
            ("SccAlertPoliciesJson", "nvarchar(max) NULL"),
            ("AzureSubscriptionsJson", "nvarchar(max) NULL"),
            ("AzureVirtualMachinesJson", "nvarchar(max) NULL"),
            ("AzureAppServicesJson", "nvarchar(max) NULL"),
            ("AzureLastCollectedAt", "datetimeoffset NULL"),
            ("AzureLastError", "nvarchar(max) NULL"),
            ("AzureReservationsJson", "nvarchar(max) NULL"),
            ("EntraAppRegistrationsJson", "nvarchar(max) NULL"),
            ("EntraServicePrincipalsJson", "nvarchar(max) NULL"),
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
                AliasesJson nvarchar(max) NULL,
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
            ("AliasesJson", "nvarchar(max) NULL"),
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
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('AzureAdConfigs', 'U') IS NULL
            CREATE TABLE AzureAdConfigs (
                Id int NOT NULL IDENTITY,
                TenantId nvarchar(max) NOT NULL,
                FrontendClientId nvarchar(max) NOT NULL,
                BackendClientId nvarchar(max) NOT NULL,
                ApiScope nvarchar(max) NOT NULL,
                EncryptedBackendClientSecret nvarchar(max) NOT NULL,
                IsConfigured bit NOT NULL,
                UpdatedAt datetimeoffset NOT NULL,
                UpdatedByEmail nvarchar(max) NOT NULL,
                CONSTRAINT PK_AzureAdConfigs PRIMARY KEY (Id)
            )
            """);
    }
}

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

// BackendClientSecret is optional on /api/settings/azure-ad (blank = keep the
// existing one) but effectively required on /api/setup/azure-ad, since
// there's no existing secret to keep the first time through — enforced in
// SaveAzureAdConfigAsync, not here, since both endpoints share this shape.
record AzureAdConfigRequest(
    string? SetupToken,
    string TenantId,
    string FrontendClientId,
    string BackendClientId,
    string? BackendClientSecret,
    string ApiScope);
