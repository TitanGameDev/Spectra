using Microsoft.EntityFrameworkCore;

namespace Spectra.Api.Data;

public class SpectraDbContext(DbContextOptions<SpectraDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<DatabaseConnectionConfig> DatabaseConnections => Set<DatabaseConnectionConfig>();
    public DbSet<CustomerUser> CustomerUsers => Set<CustomerUser>();
    public DbSet<AzureAdConfig> AzureAdConfigs => Set<AzureAdConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.EntraObjectId)
            .IsUnique();

        modelBuilder.Entity<CustomerUser>()
            .HasIndex(u => new { u.CustomerId, u.GraphUserId })
            .IsUnique();
    }
}
