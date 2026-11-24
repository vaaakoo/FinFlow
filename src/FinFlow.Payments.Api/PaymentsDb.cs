using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Payments.Api;

public sealed class PaymentsDb : DbContext
{
    public PaymentsDb(DbContextOptions<PaymentsDb> options) : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> Events => Set<PaymentEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LifecycleOutbox>().HasIndex(row => row.OccurredAt).HasFilter(""" "PublishedAt" IS NULL """);
        model.Entity<Payment>().HasIndex(payment => new { payment.Subject, payment.Key }).IsUnique();
        model.Entity<Payment>().HasIndex(payment => new { payment.Status, payment.NextAttempt });
        model.Entity<PaymentEvent>().HasKey(item => new { item.PaymentId, item.Version });
        model.Entity<PaymentEvent>().HasOne<Payment>().WithMany().HasForeignKey(item => item.PaymentId);
    }
}

public enum PaymentStatus
{
    Pending, Charged, Settled, CompensationPending, Failed, RefundPending, Refunded, RefundRejected
}

public sealed class Payment
{
    public Guid Id { get; init; }
    public required string Subject { get; init; }
    public required string Key { get; init; }
    public Guid AccountId { get; init; }
    public long AmountMinor { get; init; }
    public required string Scenario { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttempt { get; set; }
    public PaymentStatus Status { get; set; }
    public int Version { get; set; }
    public int FailedAttempts { get; set; }
    public Guid? ChargeId { get; set; }
    public Guid? RefundId { get; set; }
    public string? Reason { get; set; }

    public void MoveTo(PaymentsDb db, PaymentStatus status, string? reason = null)
    {
        Status = status;
        Reason = reason;
        FailedAttempts = 0;
        NextAttempt = DateTimeOffset.UtcNow;
        Version++;
        db.Events.Add(new PaymentEvent(Id, Version, status, NextAttempt, ChargeId, RefundId, reason));
        db.Add(LifecycleOutbox.Create("payment." + status.ToString().ToLowerInvariant(), Id.ToString()));
    }

    public PaymentView ToView() =>
        new(Id, AccountId, AmountMinor, Scenario, Status.ToString(), ChargeId, RefundId, Reason, CreatedAt);
}

public sealed record PaymentEvent(Guid PaymentId, int Version, PaymentStatus Status, DateTimeOffset OccurredAt,
    Guid? ChargeId, Guid? RefundId, string? Reason);
public sealed record PaymentView(Guid Id, Guid AccountId, long AmountMinor, string Scenario, string Status,
    Guid? ChargeId, Guid? RefundId, string? Reason, DateTimeOffset CreatedAt);
