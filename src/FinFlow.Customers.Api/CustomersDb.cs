using Microsoft.EntityFrameworkCore;

namespace FinFlow.Customers.Api;

public sealed class CustomersDb : DbContext
{
    public CustomersDb(DbContextOptions<CustomersDb> options) : base(options)
    {
    }

    public DbSet<CustomerProfile> Profiles => Set<CustomerProfile>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var profile = model.Entity<CustomerProfile>();
        profile.HasKey(p => p.Subject);
        profile.Property(p => p.Subject).HasMaxLength(128);
        profile.Property(p => p.DisplayName).HasMaxLength(100);
        profile.Property(p => p.Kyc).HasConversion<string>().HasMaxLength(16);
        profile.Property(p => p.Version).IsRowVersion();
    }
}

public enum KycStatus { Pending, Verified, Rejected }

public sealed class CustomerProfile
{
    public required string Subject { get; init; }
    public required string DisplayName { get; set; }
    public KycStatus Kyc { get; set; }
    public bool Active { get; set; } = true;
    public uint Version { get; private set; }

    public CustomerView View() => new(Subject, DisplayName, Kyc.ToString(), Active);
}

public sealed record CustomerView(string Subject, string DisplayName, string Kyc, bool Active);

