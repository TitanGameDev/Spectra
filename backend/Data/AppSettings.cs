namespace Spectra.Api.Data;

// Single-row table — there is exactly one settings record, always Id = 1.
public class AppSettings
{
    public int Id { get; set; } = 1;

    // Entra ID group Object ID whose members are treated as admins.
    // Null until an admin configures one.
    public string? AdminGroupId { get; set; }
    public string? AdminGroupDisplayName { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedByEmail { get; set; }
}
