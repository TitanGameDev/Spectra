using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

// Loads backend/.env into process environment variables (if present) so
// ASP.NET Core's built-in env-var config provider can pick up AzureAd__*
// and Cors__* keys. No-op in environments where the file doesn't exist
// (e.g. production, where real config comes from a secure vault instead).
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(allowedOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous();

app.MapGet("/api/me", (System.Security.Claims.ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        name = user.Identity?.Name,
        claims = user.Claims.Select(c => new { c.Type, c.Value }),
    });
}).RequireAuthorization();

app.Run();
