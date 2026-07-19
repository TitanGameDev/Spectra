namespace Spectra.Api.Data;

public class AppUser
{
    public int Id { get; set; }

    // Entra ID's "oid" claim — stable, unique per user per tenant.
    public required string EntraObjectId { get; set; }

    public required string DisplayName { get; set; }
    public required string Email { get; set; }

    // True only for the very first user ever to sign in — permanent admin
    // access so there's always at least one way into Settings to configure
    // the real admin group.
    public bool IsBootstrapAdmin { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
}
