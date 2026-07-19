using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Spectra.Api.Auth;
using Spectra.Api.Data;

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

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=spectra.db";
builder.Services.AddDbContext<SpectraDbContext>(options => options.UseSqlite(connectionString));
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
    scope.ServiceProvider.GetRequiredService<SpectraDbContext>().Database.Migrate();
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

app.Run();

record UpdateSettingsRequest(string? AdminGroupId, string? AdminGroupDisplayName);
