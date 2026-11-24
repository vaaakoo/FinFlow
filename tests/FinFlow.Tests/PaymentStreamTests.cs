extern alias PaymentsApi;

using Microsoft.EntityFrameworkCore;
using PaymentsApi::FinFlow.Payments.Api;

namespace FinFlow.Tests;

public sealed class PaymentStreamTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replay_restores_request_and_compensated_state_without_a_projection()
    {
        var opened = Opened();
        var charge = Guid.NewGuid();
        var compensation = Guid.NewGuid();
        var state = Payment.Replay([
            opened,
            new(opened.PaymentId, 2, PaymentStatus.Charged, Started.AddSeconds(1), charge, null, null),
            new(opened.PaymentId, 3, PaymentStatus.CompensationPending, Started.AddSeconds(2), charge, null, "Declined"),
            new(opened.PaymentId, 4, PaymentStatus.Failed, Started.AddSeconds(3), charge, compensation, "Reversed")
        ]);

        Assert.Equal(opened.Subject, state.Subject);
        Assert.Equal(opened.RequestKey, state.Key);
        Assert.Equal(opened.AccountId!.Value, state.AccountId);
        Assert.Equal(opened.AmountMinor!.Value, state.AmountMinor);
        Assert.Equal(opened.Scenario, state.Scenario);
        Assert.Equal(Started, state.CreatedAt);
        Assert.Equal(PaymentStatus.Failed, state.Status);
        Assert.Equal(4, state.Version);
        Assert.Equal(charge, state.ChargeId);
        Assert.Equal(compensation, state.RefundId);
        Assert.Equal("Reversed", state.Reason);
    }

    [Fact]
    public void Retry_appends_a_version_without_changing_status_or_publishing_a_transition()
    {
        using var db = CreateDb();
        var opened = Opened();
        var charge = Guid.NewGuid();
        var charged = new PaymentEvent(opened.PaymentId, 2, PaymentStatus.Charged, Started, charge, null, null);
        var state = Payment.Replay([opened, charged]);

        state.ScheduleRetry(db, Started);
        var retry = Assert.Single(db.ChangeTracker.Entries<PaymentEvent>()).Entity;
        var restored = Payment.Replay([opened, charged, retry]);
        Assert.Equal(PaymentStatus.Charged, restored.Status);
        Assert.Equal(charge, restored.ChargeId);
        Assert.Equal(3, restored.Version);
        Assert.Equal(1, restored.FailedAttempts);
        Assert.Equal(Started.AddSeconds(2), restored.NextAttempt);
        Assert.Single(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Replay_rejects_a_missing_version_or_a_different_payment()
    {
        var opened = Opened();
        Assert.Throws<InvalidOperationException>(() => Payment.Replay([
            opened, new(opened.PaymentId, 3, PaymentStatus.Settled, Started, null, null, null)
        ]));
        Assert.Throws<InvalidOperationException>(() => Payment.Replay([
            opened, new(Guid.NewGuid(), 2, PaymentStatus.Charged, Started, null, null, null)
        ]));
        Assert.Throws<InvalidOperationException>(() => Payment.Replay([opened with { Subject = null }]));
    }

    [Fact]
    public void Projection_rejects_a_stale_writer_and_ef_checks_the_original_version()
    {
        using var db = CreateDb();
        var opened = Opened();
        var charge = Guid.NewGuid();
        var charged = new PaymentEvent(opened.PaymentId, 2, PaymentStatus.Charged, Started, charge, null, null);
        var projection = Payment.Replay([opened, charged]);
        db.Attach(projection);
        var state = Payment.Replay([opened, charged]);
        state.MoveTo(db, PaymentStatus.Settled);

        projection.UpdateProjection(state, 2);
        Assert.Equal(PaymentStatus.Settled, projection.Status);
        Assert.Throws<DbUpdateConcurrencyException>(() => projection.UpdateProjection(state, 2));
        var version = db.Entry(projection).Property(payment => payment.Version);
        Assert.True(version.Metadata.IsConcurrencyToken);
        Assert.Equal(2, version.OriginalValue);
        Assert.Equal(3, version.CurrentValue);
        Assert.Equal(3, Assert.Single(db.ChangeTracker.Entries<PaymentEvent>()).Entity.Version);
    }

    private static PaymentEvent Opened() =>
        new(Guid.NewGuid(), 1, PaymentStatus.Pending, Started, null, null, null)
        {
            Subject = "test-customer", RequestKey = "test-payment", AccountId = Guid.NewGuid(),
            AmountMinor = 1234, Scenario = "uncertain"
        };

    private static PaymentsDb CreateDb() => new(new DbContextOptionsBuilder<PaymentsDb>()
        .UseNpgsql("Host=localhost;Database=unused").Options);
}
