using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Spectra.Api.Data;
using Spectra.Api.Services;

namespace Spectra.Api.Auth;

// Adds a "spectra_admin" claim ("true"/"false") to every authenticated request,
// computed from:
//   1. Bootstrap admin — the very first user ever to sign in, permanently.
//   2. Membership in the configured admin group, read straight from the
//      token's "groups" claim (added via the backend App Registration's token
//      configuration — see README). Doesn't handle the 200+ group overage
//      case; the token falls back to a distributed-claim indicator instead of
//      listing groups directly, which is out of scope for an MSP-internal
//      tool of this size.
// Also upserts the AppUser row so we always have an up-to-date name/email.
public class SpectraClaimsTransformation(SpectraDbContext db, DatabaseHealth databaseHealth) : IClaimsTransformation
{
    private const string AdminClaimType = "spectra_admin";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        // TransformAsync can run more than once per request; don't redo the work.
        if (identity.HasClaim(c => c.Type == AdminClaimType))
        {
            return principal;
        }

        var objectId = principal.FindFirstValue("oid") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(objectId))
        {
            identity.AddClaim(new Claim(AdminClaimType, "false"));
            return principal;
        }

        var displayName = principal.FindFirstValue("name") ?? principal.Identity?.Name ?? "";
        var email = principal.FindFirstValue(ClaimTypes.Upn) ?? principal.FindFirstValue("preferred_username") ?? "";

        try
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == objectId);
            if (user is null)
            {
                var isFirstUserEver = !await db.Users.AnyAsync();
                user = new AppUser
                {
                    EntraObjectId = objectId,
                    DisplayName = displayName,
                    Email = email,
                    IsBootstrapAdmin = isFirstUserEver,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            else if (user.DisplayName != displayName || user.Email != email)
            {
                user.DisplayName = displayName;
                user.Email = email;
                await db.SaveChangesAsync();
            }

            var settings = await db.Settings.SingleOrDefaultAsync();
            var tokenGroups = principal.FindAll("groups").Select(c => c.Value).ToHashSet();
            var isAdmin = user.IsBootstrapAdmin
                || (settings?.AdminGroupId is { } adminGroupId && tokenGroups.Contains(adminGroupId));

            databaseHealth.MarkHealthy();
            identity.AddClaim(new Claim(AdminClaimType, isAdmin ? "true" : "false"));
        }
        catch (Exception ex)
        {
            // The database is unreachable or missing its schema (dropped tables,
            // wrong credentials, server down, etc). Don't let that crash the whole
            // authentication pipeline — admin status genuinely can't be determined
            // without the database, but every request still needs to get through
            // so the frontend's database-setup screen (driven by /api/system/status)
            // can actually be reached and used to fix it.
            databaseHealth.MarkUnhealthy(ex.Message);
            identity.AddClaim(new Claim(AdminClaimType, "false"));
        }

        return principal;
    }
}
