using Microsoft.EntityFrameworkCore;

namespace Spectra.Api.Data;

public class SpectraDbContext(DbContextOptions<SpectraDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<DatabaseConnectionConfig> DatabaseConnections => Set<DatabaseConnectionConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.EntraObjectId)
            .IsUnique();
    }
}
