namespace Spectra.Api.Data;

public class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedByEmail { get; set; }
}
