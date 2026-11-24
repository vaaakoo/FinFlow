using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Transfers.Api;

public sealed class TransfersDb : DbContext
{
    public TransfersDb(DbContextOptions<TransfersDb> options) : base(options)
    {
    }

    public DbSet<TransferOrder> Orders => Set<TransferOrder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LifecycleOutbox>().HasIndex(row => row.OccurredAt).HasFilter(""" "PublishedAt" IS NULL """);
        var order = model.Entity<TransferOrder>();
        order.Property(t => t.Subject).HasMaxLength(128);
        order.Property(t => t.Key).HasMaxLength(128);
        order.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        order.HasIndex(t => new { t.Subject, t.Key }).IsUnique();
        order.HasIndex(t => new { t.Subject, t.CreatedAt, t.Id });
        order.HasIndex(t => new { t.NextAttempt, t.Id }).HasFilter(""" "Status" = 'Pending' """);
    }
}

public enum OrderStatus { Pending, Completed, Rejected }

public sealed class TransferOrder
{
    public Guid Id { get; init; }
    public required string Subject { get; init; }
    public required string Key { get; init; }
    public Guid SourceAccountId { get; init; }
    public Guid DestinationAccountId { get; init; }
    public long AmountMinor { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? TraceParent { get; init; }
    public OrderStatus Status { get; set; }
    public Guid? PostingId { get; set; }
    public string? Rejection { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttempt { get; set; }

    public OrderView View() => new(Id, SourceAccountId, DestinationAccountId, AmountMinor, CreatedAt,
        Status.ToString(), PostingId, Rejection, "GEL");
}

public sealed record OrderView(Guid Id, Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor,
    DateTimeOffset CreatedAt, string Status, Guid? PostingId, string? Rejection, string Currency);

