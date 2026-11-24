using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Payments.Api;

public sealed class PaymentsDb : DbContext
{
    public PaymentsDb(DbContextOptions<PaymentsDb> options) : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> Events => Set<PaymentEvent>();

    public async Task<Payment?> ReplayAsync(Guid paymentId, CancellationToken token)
    {
        var events = await Events.AsNoTracking().Where(item => item.PaymentId == paymentId)
            .OrderBy(item => item.Version).ToListAsync(token);
        return events.Count == 0 ? null : Payment.Replay(events);
    }

    public async Task<Payment> ReplayForWriteAsync(Payment projection, CancellationToken token)
    {
        var payment = await ReplayAsync(projection.Id, token);
        if (payment is null)
            throw new InvalidOperationException("Payment is missing its event stream.");
        projection.CheckVersion(payment.Version);
        return payment;
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LifecycleOutbox>().HasIndex(row => row.OccurredAt).HasFilter(""" "PublishedAt" IS NULL """);
        model.Entity<Payment>().HasIndex(payment => new { payment.Subject, payment.Key }).IsUnique();
        model.Entity<Payment>().HasIndex(payment => new { payment.Status, payment.NextAttempt });
        model.Entity<Payment>().Property(payment => payment.Version).IsConcurrencyToken();
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

    public static Payment Replay(IReadOnlyList<PaymentEvent> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Payment stream is empty.");

        var opened = events[0];
        if (opened.Version != 1 || opened.Status != PaymentStatus.Pending
            || opened.Subject is null || opened.RequestKey is null || opened.AccountId is null
            || opened.AmountMinor is null || opened.Scenario is null)
            throw new InvalidOperationException("Payment stream is missing a valid opening event.");

        var payment = new Payment
        {
            Id = opened.PaymentId,
            Subject = opened.Subject,
            Key = opened.RequestKey,
            AccountId = opened.AccountId.Value,
            AmountMinor = opened.AmountMinor.Value,
            Scenario = opened.Scenario,
            CreatedAt = opened.OccurredAt
        };
        foreach (var item in events)
        {
            if (item.PaymentId != payment.Id || item.Version != payment.Version + 1)
                throw new InvalidOperationException("Payment stream versions are not contiguous.");

            payment.Version = item.Version;
            payment.Status = item.Status;
            payment.ChargeId = item.ChargeId;
            payment.RefundId = item.RefundId;
            payment.Reason = item.Reason;
            payment.NextAttempt = item.NextAttempt ?? item.OccurredAt;
            payment.FailedAttempts = item.FailedAttempts;
        }
        return payment;
    }

    internal void CheckVersion(int expectedVersion)
    {
        if (Version != expectedVersion)
            throw new DbUpdateConcurrencyException("Payment changed since the expected stream version.");
    }

    internal void UpdateProjection(Payment payment, int expectedVersion)
    {
        CheckVersion(expectedVersion);
        if (payment.Id != Id || payment.Version != expectedVersion + 1)
            throw new InvalidOperationException("A payment update must append exactly one event.");

        Version = payment.Version;
        Status = payment.Status;
        ChargeId = payment.ChargeId;
        RefundId = payment.RefundId;
        Reason = payment.Reason;
        NextAttempt = payment.NextAttempt;
        FailedAttempts = payment.FailedAttempts;
    }

    public void MoveTo(PaymentsDb db, PaymentStatus status, string? reason = null)
    {
        Status = status;
        Reason = reason;
        FailedAttempts = 0;
        var now = DateTimeOffset.UtcNow;
        NextAttempt = now;
        AppendEvent(db, now);
        db.Add(LifecycleOutbox.Create("payment." + status.ToString().ToLowerInvariant(), Id.ToString()));
    }

    internal void ScheduleRetry(PaymentsDb db, DateTimeOffset now)
    {
        FailedAttempts = Math.Min(FailedAttempts + 1, 8);
        NextAttempt = now.AddSeconds(Math.Min(60, Math.Pow(2, FailedAttempts)));
        AppendEvent(db, now);
    }

    private void AppendEvent(PaymentsDb db, DateTimeOffset occurredAt)
    {
        Version++;
        db.Events.Add(new PaymentEvent(Id, Version, Status, occurredAt, ChargeId, RefundId, Reason)
        {
            NextAttempt = NextAttempt,
            FailedAttempts = FailedAttempts
        });
    }

    public PaymentView ToView() =>
        new(Id, AccountId, AmountMinor, Scenario, Status.ToString(), ChargeId, RefundId, Reason, CreatedAt);
}

public sealed record PaymentEvent(Guid PaymentId, int Version, PaymentStatus Status, DateTimeOffset OccurredAt,
    Guid? ChargeId, Guid? RefundId, string? Reason)
{
    public string? Subject { get; init; }
    public string? RequestKey { get; init; }
    public Guid? AccountId { get; init; }
    public long? AmountMinor { get; init; }
    public string? Scenario { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public int FailedAttempts { get; init; }
}
public sealed record PaymentView(Guid Id, Guid AccountId, long AmountMinor, string Scenario, string Status,
    Guid? ChargeId, Guid? RefundId, string? Reason, DateTimeOffset CreatedAt);
