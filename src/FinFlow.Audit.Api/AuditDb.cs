using Microsoft.EntityFrameworkCore;

namespace FinFlow.Audit.Api;

public sealed class AuditDb : DbContext
{
    public AuditDb(DbContextOptions<AuditDb> options) : base(options)
    {
    }

    public DbSet<TransferAudit> TransferEvents => Set<TransferAudit>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TransferAudit>().HasKey(entry => entry.EventId);
        model.Entity<TransferAudit>().HasIndex(entry => new { entry.CompletedAt, entry.EventId });
        model.Entity<TransferAudit>().HasIndex(entry => entry.TransferId);
        model.Entity<TransferAudit>().Property(entry => entry.CorrelationId).HasMaxLength(64);
        model.Entity<TransferAudit>().Property(entry => entry.CausationId).HasMaxLength(64);
    }
}

public sealed class TransferAudit
{
    public Guid EventId { get; init; }
    public Guid TransferId { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
}

