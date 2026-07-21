using System.Security.Claims;
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
    if (active.Kind is "sqlserver" or "mysql")
    {
        // SQL Server/MySQL databases configured through Settings are provisioned
        // with EnsureCreated (see the /provision endpoint), not migrations — this
        // just covers the case where the app restarts after a prior cutover.
        db.Database.EnsureCreated();
    }
    else
    {
        db.Database.Migrate();
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
    var customers = await db.Customers
        .OrderBy(c => c.Name)
        .Select(c => new { c.Id, c.Name, c.CreatedAt, c.CreatedByEmail })
        .ToListAsync();
    return Results.Ok(customers);
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/customers", async (CreateCustomerRequest request, ClaimsPrincipal user, SpectraDbContext db) =>
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

    var customer = new Customer
    {
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedByEmail = user.FindFirstValue(ClaimTypes.Upn) ?? user.FindFirstValue("preferred_username") ?? "",
    };
    db.Customers.Add(customer);
    await db.SaveChangesAsync();

    return Results.Created($"/api/customers/{customer.Id}", new
    {
        customer.Id,
        customer.Name,
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

        // Copy existing data so admin access, the admin group setting, and customers
        // all survive the cutover — otherwise the admin doing this could lose access
        // the moment the switch happens.
        var users = await db.Users.AsNoTracking().ToListAsync();
        var settingsRows = await db.Settings.AsNoTracking().ToListAsync();
        var customers = await db.Customers.AsNoTracking().ToListAsync();

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
            targetDb.Customers.AddRange(customers.Select(c => new Customer
            {
                Name = c.Name,
                CreatedAt = c.CreatedAt,
                CreatedByEmail = c.CreatedByEmail,
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

record UpdateSettingsRequest(string? AdminGroupId, string? AdminGroupDisplayName);
record CreateCustomerRequest(string? Name);
record DatabaseConnectionRequest(string DatabaseType, string Host, int Port, string DatabaseName, string Username, string Password);
