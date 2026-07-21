using System.Security.Claims;
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
    // Minimal — just enough for every signed-in user's customer switcher dropdown.
    var customers = await db.Customers
        .OrderBy(c => c.Name)
        .Select(c => new { c.Id, c.Name })
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
    }));
}).RequireAuthorization();

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
    await CollectCustomerDataAsync(customer, db, graphClient, logger);

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
    ILogger<Program> logger) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    await CollectCustomerDataAsync(customer, db, graphClient, logger);

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
static async Task CollectCustomerDataAsync(Customer customer, SpectraDbContext db, GraphAppClient graphClient, ILogger logger)
{
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

        Dictionary<string, GraphMailboxUsageDto> mailboxByUpn = new(StringComparer.OrdinalIgnoreCase);
        string? mailboxWarning = null;
        try
        {
            mailboxByUpn = await graphClient.GetMailboxUsageByUpnAsync(customer.TenantId, token);
        }
        catch (GraphPermissionDeniedException ex)
        {
            logger.LogWarning(ex, "Reports.Read.All not yet effective for tenant {TenantId}", customer.TenantId);
            mailboxWarning = "Users and licenses synced, but mailbox data is unavailable: the Reports.Read.All " +
                "permission isn't granted yet for this tenant — click Grant consent again and re-collect.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get mailbox usage for tenant {TenantId}", customer.TenantId);
            // Not a permission problem — don't tell the admin to re-consent when
            // that isn't the actual fix; show Graph's own explanation instead.
            mailboxWarning = $"Users and licenses synced, but mailbox data is unavailable: {ex.Message}";
        }

        var existing = await db.CustomerUsers.Where(u => u.CustomerId == customer.Id).ToListAsync();
        db.CustomerUsers.RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        db.CustomerUsers.AddRange(graphUsers.Select(u =>
        {
            var mailbox = mailboxByUpn.GetValueOrDefault(u.UserPrincipalName);
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
                SyncedAt = now,
            };
        }));

        customer.ConsentGranted = true;
        customer.LastSyncedAt = now;
        customer.LastSyncError = mailboxWarning;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to collect data for customer {CustomerId} ({TenantId})", customer.Id, customer.TenantId);
        customer.LastSyncError = ex.Message;
    }

    await db.SaveChangesAsync();
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

record StoredLicense(string SkuId, string SkuPartNumber);

record UpdateSettingsRequest(string? AdminGroupId, string? AdminGroupDisplayName);
record CreateCustomerRequest(string? Name, string? TenantId);
record DatabaseConnectionRequest(string DatabaseType, string Host, int Port, string DatabaseName, string Username, string Password);
